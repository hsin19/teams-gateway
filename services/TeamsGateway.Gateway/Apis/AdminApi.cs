using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Apis;

/// <summary><c>/admin</c>：綁定帳號、管理 API key。帶 <c>X-Admin-Token</c>。</summary>
public static class AdminApi
{
    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminTokenFilter>().WithTags("Admin");

        // 第一次綁定：把瀏覽器匯出的登入 cookie（spike/http_refresh.py export 的格式）PUT 進來
        admin.MapPut("/login-cookies", async Task<Results<Ok<ImportedCookies>, BadRequest<ApiError>>> (HttpRequest request, SecureStore store, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync(ct);
            List<LoginCookie>? cookies;
            try
            {
                cookies = JsonSerializer.Deserialize(json, TeamsJsonContext.Default.ListLoginCookie);
            }
            catch (JsonException)
            {
                return TypedResults.BadRequest(new ApiError("不是合法的 cookie JSON 陣列"));
            }
            if (cookies is not { Count: > 0 })
                return TypedResults.BadRequest(new ApiError("cookie 陣列是空的"));

            await store.SetAsync(TeamsAuthConstants.CookieSecureKey, json, ct);
            return TypedResults.Ok(new ImportedCookies(cookies.Count));
        })
        .WithSummary("匯入登入 cookie")
        .WithDescription("一次性 bootstrap：把瀏覽器匯出的 Teams 登入 cookie 加密存進來，之後自動換 skypetoken。約一年效期，到期重匯。");

        admin.MapGet("/auth", async (SkypeTokenProvider tokens, SecureStore store, CancellationToken ct) =>
        {
            var hasCookies = await store.GetAsync(TeamsAuthConstants.CookieSecureKey, ct) is not null;
            var current = tokens.Current;
            return TypedResults.Ok(new AuthInfo(hasCookies, current?.SkypeId, current?.ExpiresAt));
        })
        .WithSummary("看認證狀態");

        admin.MapPost("/auth/refresh", async Task<Results<Ok<TokenInfo>, ProblemHttpResult>> (SkypeTokenProvider tokens, CancellationToken ct) =>
        {
            // 一定要真的重換：GetAsync 會回快取，cookie 已經死了也照樣回 ok
            try
            {
                var token = await tokens.RefreshAsync(ct: ct);
                return TypedResults.Ok(new TokenInfo(token.SkypeId, token.ExpiresAt));
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.Problem($"換不到 skypetoken：{ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .WithSummary("強制換一次 skypetoken")
        .WithDescription("驗證 cookie 還有效（換得到 token 就 ok，失敗代表該重匯）。");

        admin.MapPost("/api-keys", async Task<Results<Ok<CreatedApiKey>, BadRequest<ApiError>>> (CreateApiKeyRequest body, ApiKeyService keys, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return TypedResults.BadRequest(new ApiError("name 必填"));
            var (key, raw) = await keys.CreateAsync(body.Name, ct);
            // 明文只回這一次
            return TypedResults.Ok(new CreatedApiKey(key.Id, key.Name, raw));
        })
        .WithSummary("建 API key")
        .WithDescription("給訂閱者（relay / client）打 /v1 用。回的 `apiKey` 明文只出現這一次。");

        admin.MapGet("/api-keys", async (ApiKeyService keys, CancellationToken ct) =>
        {
            var all = await keys.ListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<ApiKeyInfo>>(all.Select(k => new ApiKeyInfo(k.Id, k.Name, k.CreatedAt, k.RevokedAt)).ToList());
        })
        .WithSummary("列出 API key");

        admin.MapDelete("/api-keys/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, ApiKeyService keys, CancellationToken ct) =>
            await keys.RevokeAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound())
        .WithSummary("撤銷 API key");

        // 訂閱對話：gateway 只追 / 存訂閱中的對話（Trouter 會推帳號所有對話，但只有這些入庫）
        admin.MapGet("/subscriptions", async (IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var subs = await db.Conversations.Where(c => c.Subscribed)
                .Select(c => new SubscriptionInfo(c.Id, c.Topic, c.LastMessageId, c.UpdatedAt)).ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<SubscriptionInfo>>(subs);
        })
        .WithSummary("列出訂閱中的對話");

        // chat 可給 id 或群名；訂閱後同步回補歷史（預設一週）
        admin.MapPut("/subscriptions", async Task<Results<Ok<SubscribedConversation>, NotFound<ApiError>, BadRequest<ApiError>>> (
            SubscribeRequest body, ChatsvcClient chatsvc, IDbContextFactory<GatewayDbContext> factory, Backfiller backfiller, CancellationToken ct) =>
        {
            var chats = await chatsvc.GetConversationsAsync(ct);
            ConversationDto hit;
            if (ChatsvcClient.IsThreadId(body.Chat))
                hit = chats.FirstOrDefault(c => c.Id == body.Chat) ?? new ConversationDto(body.Chat, null, null, null);
            else
            {
                var hits = ChatsvcClient.MatchByName(chats, body.Chat);
                if (hits.Count == 0)
                    return TypedResults.NotFound(new ApiError($"找不到對話 '{body.Chat}'"));
                if (hits.Count > 1)
                    return TypedResults.BadRequest(new ApiError(ChatsvcClient.Ambiguous(body.Chat, hits)));
                hit = hits[0];
            }

            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var conv = await db.Conversations.FindAsync([hit.Id], ct);
                if (conv is null)
                    db.Conversations.Add(new Conversation { Id = hit.Id, Topic = hit.Topic, Subscribed = true, UpdatedAt = DateTimeOffset.UtcNow });
                else
                {
                    // 重新訂閱：沒追的那段期間不該當成「漏接」發給訂閱者，水位線歸零 → 回補只存不發
                    if (!conv.Subscribed)
                        conv.SyncedThrough = null;
                    conv.Subscribed = true;
                    conv.Topic = hit.Topic ?? conv.Topic;
                    conv.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(ct);
            }

            await backfiller.BackfillAsync(hit.Id, publishGap: false, ct); // 回補歷史（只存不發）
            return TypedResults.Ok(new SubscribedConversation(hit.Id, hit.Topic));
        })
        .WithSummary("訂閱對話")
        .WithDescription("`chat` 可給對話 id 或群名。訂閱後同步回補歷史（預設一週），之後即時訊息進 SSE / webhook。");

        admin.MapDelete("/subscriptions/{id}", async Task<Results<NoContent, NotFound>> (string id, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var conv = await db.Conversations.FindAsync([id], ct);
            if (conv is null || !conv.Subscribed)
                return TypedResults.NotFound();
            conv.Subscribed = false; // 保留已存的歷史
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
        .WithSummary("取消訂閱")
        .WithDescription("停止追這個對話，但保留已存的歷史。");

        return admin;
    }
}
