using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace TeamsGateway.Gateway.Teams;

/// <summary>用帳號（MRI）查 Teams 上現在的顯示名稱。</summary>
public interface IPeopleDirectory
{
    /// <summary>查得到的才在結果裡；查不到、或查詢失敗就少那幾個，不丟例外。</summary>
    Task<IReadOnlyDictionary<string, string>> DisplayNamesAsync(IReadOnlyCollection<string> mris, CancellationToken ct);
}

/// <summary>
/// 打網頁版遇到不認識的人時用的 <c>mt/beta/users/fetchShortProfile</c>（spike/profiles.py）：一次查多個，
/// 要 MT 的 Bearer token 加 skypetoken（只帶 skypetoken 會 401）。查到的記一天——名字很少改，同一個人不重複查。
/// 呼叫端（ingest）握著寫入鎖在等：每次查詢限 <see cref="Budget"/>，超過就當查不到，不拖住其他訊息。
/// 換 token 不限時——換到一半取消可能弄丟輪換過的登入 cookie，而且 token 本來別人也要用。
/// </summary>
public sealed class PeopleDirectory(IHttpClientFactory httpFactory, SkypeTokenProvider tokens, ILogger<PeopleDirectory> logger) : IPeopleDirectory
{
    internal const string Url = "https://teams.live.com/api/mt/beta/users/fetchShortProfile"
        + "?isMailAddress=false&enableGuest=true&skypeTeamsInfo=true&canBeSmtpAddress=false"
        + "&includeIBBarredUsers=false&includeDisabledAccounts=false&ggEnabled=true";

    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private const int MaxCached = 5000;

    private readonly ConcurrentDictionary<string, (string Name, DateTimeOffset At)> _cache = new();

    public async Task<IReadOnlyDictionary<string, string>> DisplayNamesAsync(IReadOnlyCollection<string> mris, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var mri in mris.Distinct())
        {
            if (_cache.TryGetValue(mri, out var hit) && now - hit.At < CacheFor)
                result[mri] = hit.Name;
            else
                missing.Add(mri);
        }
        if (missing.Count == 0)
            return result;

        try
        {
            var http = httpFactory.CreateClient(nameof(PeopleDirectory));
            var token = await tokens.GetAsync(ct);
            var fetched = await FetchWithinBudgetAsync(http, token, missing, ct);
            if (fetched is null) // 401：token 過期，換一次再試
            {
                token = await tokens.RefreshAsync(token.Token, ct);
                fetched = await FetchWithinBudgetAsync(http, token, missing, ct) ?? new Dictionary<string, string>();
            }
            if (_cache.Count >= MaxCached)
                _cache.Clear();
            foreach (var (mri, name) in fetched)
            {
                _cache[mri] = (name, now);
                result[mri] = name;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "查 {Count} 個人的顯示名稱失敗", missing.Count);
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, string>?> FetchWithinBudgetAsync(
        HttpClient http, SkypeToken token, IReadOnlyCollection<string> mris, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        return await FetchAsync(http, token, mris, budget.Token);
    }

    /// <summary>打一次 fetchShortProfile；401 回 null 讓呼叫端換 token 重試。token 沒帶 MT token（舊的）就查不了，回空的。</summary>
    internal static async Task<IReadOnlyDictionary<string, string>?> FetchAsync(
        HttpClient http, SkypeToken token, IReadOnlyCollection<string> mris, CancellationToken ct)
    {
        if (token.MtToken is null)
            return new Dictionary<string, string>();

        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = JsonContent.Create(mris.ToList(), TeamsJsonContext.Default.ListString),
        };
        request.Headers.TryAddWithoutValidation("authorization", $"Bearer {token.MtToken}");
        request.Headers.TryAddWithoutValidation("x-skypetoken", token.Token);
        request.Headers.TryAddWithoutValidation("x-ms-client-type", "cdlworker");
        request.Headers.TryAddWithoutValidation("x-ms-client-version", TeamsAuthConstants.ClientVersion);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        response.EnsureSuccessStatusCode();

        var profiles = await response.Content.ReadFromJsonAsync(TeamsJsonContext.Default.ShortProfiles, ct);
        var names = new Dictionary<string, string>();
        foreach (var profile in profiles?.Value ?? [])
            if (profile is { Mri: { } mri, DisplayName: { } name } && !string.IsNullOrWhiteSpace(name))
                names[mri] = name;
        return names;
    }
}
