using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using static TeamsGateway.Gateway.Teams.TeamsAuthConstants;

namespace TeamsGateway.Gateway.Teams;

/// <summary>一筆存下來的登入 cookie（來自瀏覽器 profile 匯出）。只需要這幾個欄位。</summary>
public sealed record LoginCookie(string Name, string Value, string Domain, string? Path);

/// <summary>
/// skypetoken（chatsvc / Trouter 用）與一起換到的 MT token（<c>api/mt</c> 端點用，例如用帳號查人名）。
/// 兩顆都約 8 小時；<see cref="ExpiresAt"/> 取兩者較早的，一起換。
/// </summary>
public sealed record SkypeToken(string Token, string SkypeId, DateTimeOffset ExpiresAt, string? MtToken = null);

/// <summary>
/// 用登入 cookie 經純 HTTP 換到 skypetoken，並快取到快過期才重換。整個流程不需要瀏覽器。
/// 對應 spike/FINDINGS.md「Token（純 HTTP）」一節。
/// </summary>
public sealed class SkypeTokenProvider(SecureStore secureStore, ILogger<SkypeTokenProvider> logger)
{
    private static readonly string[] CookieDomainHints = ["login.live.com", "login.microsoftonline.com", "live.com", "microsoftonline.com"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SkypeToken? _cached;

    public SkypeToken? Current => _cached;

    // Teams 認證健康：給 /v1/status，讓 relay 偵測 cookie 失效並告警
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>最近一次換 token 的結果是成功（且之後沒再失敗）。從沒換過 = 尚未 bootstrap，也視為不健康。</summary>
    public bool Healthy => LastSuccessAt is { } success && (LastFailureAt is not { } failure || success >= failure);

    public async Task<SkypeToken> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } hit && hit.ExpiresAt - RefreshSkew > DateTimeOffset.UtcNow)
            return hit;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } hit2 && hit2.ExpiresAt - RefreshSkew > DateTimeOffset.UtcNow)
                return hit2;
            _cached = await MintTrackedAsync(ct);
            logger.LogInformation("換到新的 skypetoken，skypeid={SkypeId}，到期 {ExpiresAt:u}", _cached.SkypeId, _cached.ExpiresAt);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 強制重換（例如打 chatsvc 收到 401 時）。帶上被拒的 <paramref name="rejectedToken"/>：好幾個請求同時 401 時，
    /// 等鎖期間別人已經換到新的就直接用，不會每個都重跑一次登入流程（連打 login.microsoftonline.com 會被限流）。
    /// </summary>
    public async Task<SkypeToken> RefreshAsync(string? rejectedToken = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (CanReuse(_cached, rejectedToken, DateTimeOffset.UtcNow))
                return _cached!;
            _cached = await MintTrackedAsync(ct);
            logger.LogInformation("強制換 skypetoken，skypeid={SkypeId}", _cached.SkypeId);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>快取的 token 不是被拒的那個、而且還沒快過期 → 別人剛換過，不用再換。沒指定被拒的（admin 強制換）一律重換。</summary>
    internal static bool CanReuse(SkypeToken? cached, string? rejectedToken, DateTimeOffset now) =>
        rejectedToken is not null && cached is { } c && c.Token != rejectedToken && c.ExpiresAt - RefreshSkew > now;

    /// <summary>包住 <see cref="MintAsync"/>，記下成功 / 失敗時間給健康狀態用。</summary>
    private async Task<SkypeToken> MintTrackedAsync(CancellationToken ct)
    {
        try
        {
            var token = await MintAsync(ct);
            LastSuccessAt = DateTimeOffset.UtcNow;
            LastError = null;
            return token;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) // 呼叫端自己取消（斷線 / 關機）不算認證失敗
        {
            LastFailureAt = DateTimeOffset.UtcNow;
            LastError = ex.Message;
            throw;
        }
    }

    private async Task<SkypeToken> MintAsync(CancellationToken ct)
    {
        var cookiesJson = await secureStore.GetAsync(CookieSecureKey, ct)
            ?? throw new InvalidOperationException("還沒匯入登入 cookie（PUT /admin/login-cookies）");

        var container = BuildCookieContainer(cookiesJson);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = container, UseCookies = true };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

        var (verifier, challenge) = Pkce();
        var code = await SilentAuthorizeAsync(http, challenge, ct);

        using var tokenResp = await PostFormAsync(http, Token, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = LoginScope,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["client_info"] = "1",
        }, origin: true, ct);
        var refreshToken = tokenResp.RootElement.GetProperty("refresh_token").GetString()!;
        // 這一步本身就是 MT（mtsvc）服務的 token：留著給 api/mt 端點用（例如查人名），不用另外換
        var mtToken = tokenResp.RootElement.GetProperty("access_token").GetString()!;
        var mtExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
            tokenResp.RootElement.TryGetProperty("expires_in", out var expiresIn) && expiresIn.ValueKind == JsonValueKind.Number && expiresIn.TryGetInt32(out var seconds) ? seconds : 3600);

        using var authResp = await PostFormAsync(http, Token, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = AuthScope,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["client_info"] = "1",
        }, origin: true, ct);
        var accessToken = authResp.RootElement.GetProperty("access_token").GetString()!;

        var skype = await AuthzConsumerAsync(http, accessToken, ct);

        await SaveCookiesAsync(container, ct);
        return skype with { MtToken = mtToken, ExpiresAt = skype.ExpiresAt < mtExpiresAt ? skype.ExpiresAt : mtExpiresAt };
    }

    private async Task<string> SilentAuthorizeAsync(HttpClient http, string challenge, CancellationToken ct)
    {
        var url = QueryHelpers.AddQueryString(Authorize, new Dictionary<string, string?>
        {
            ["client_id"] = ClientId,
            ["scope"] = LoginScope,
            ["redirect_uri"] = RedirectUri,
            ["response_mode"] = "fragment",
            ["response_type"] = "code",
            ["prompt"] = "none",
            ["nonce"] = Guid.NewGuid().ToString(),
            ["state"] = Guid.NewGuid().ToString(),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

        for (var hop = 0; hop < 10; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode is not (>= HttpStatusCode.MovedPermanently and < (HttpStatusCode)400))
                throw new InvalidOperationException($"靜默授權停在 HTTP {(int)resp.StatusCode}（登入 cookie 可能已失效，要重新匯入）");

            var location = resp.Headers.Location ?? throw new InvalidOperationException("授權 redirect 少了 Location");
            var next = new Uri(new Uri(url), location);
            if (next.GetLeftPart(UriPartial.Path).StartsWith(RedirectUri, StringComparison.Ordinal))
            {
                var fragment = QueryHelpers.ParseQuery(next.Fragment.TrimStart('#'));
                if (fragment.TryGetValue("code", out var code))
                    return code.ToString();
                throw new InvalidOperationException($"授權回來沒有 code：{string.Join(",", fragment.Keys)}");
            }
            url = next.ToString();
        }
        throw new InvalidOperationException("靜默授權 redirect 次數過多");
    }

    private static async Task<JsonDocument> PostFormAsync(HttpClient http, string url, Dictionary<string, string> form, bool origin, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        if (origin)
            req.Headers.TryAddWithoutValidation("Origin", "https://teams.live.com");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {url} 失敗 HTTP {(int)resp.StatusCode}: {Truncate(body)}");
        return JsonDocument.Parse(body);
    }

    private static async Task<SkypeToken> AuthzConsumerAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Authz) { Content = new StringContent("", Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        req.Headers.TryAddWithoutValidation("x-ms-client-type", "web");
        req.Headers.TryAddWithoutValidation("x-ms-client-version", ClientVersion);
        req.Headers.TryAddWithoutValidation("ms-teams-authz-type", "ExplicitLogin");
        req.Headers.TryAddWithoutValidation("claimschallengecapable", "true");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"authz/consumer 失敗 HTTP {(int)resp.StatusCode}: {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        var st = doc.RootElement.GetProperty("skypeToken");
        return new SkypeToken(
            st.GetProperty("skypetoken").GetString()!,
            st.GetProperty("skypeid").GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(st.GetProperty("expiresIn").GetInt32()));
    }

    private static CookieContainer BuildCookieContainer(string cookiesJson)
    {
        var container = new CookieContainer();
        var cookies = JsonSerializer.Deserialize(cookiesJson, TeamsJsonContext.Default.ListLoginCookie) ?? [];
        foreach (var c in cookies)
        {
            var domain = c.Domain.TrimStart('.');
            var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
            try
            {
                container.Add(new Cookie(c.Name, c.Value, path, domain));
            }
            catch (CookieException)
            {
                container.Add(new Cookie(c.Name, "\"" + c.Value + "\"", path, domain));
            }
        }
        return container;
    }

    private async Task SaveCookiesAsync(CookieContainer container, CancellationToken ct)
    {
        var cookies = container.GetAllCookies()
            .Where(c => CookieDomainHints.Any(h => c.Domain.Contains(h, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new LoginCookie(c.Name, c.Value.Trim('"'), c.Domain, c.Path))
            .ToList();
        if (cookies.Count > 0)
            await secureStore.SetAsync(CookieSecureKey, JsonSerializer.Serialize(cookies, TeamsJsonContext.Default.ListLoginCookie), ct);
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
