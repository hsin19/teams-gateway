using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Apis;

/// <summary><c>/v1</c>：給訂閱者的 REST。帶 API key（<c>Authorization: Bearer tgw_...</c>）。</summary>
public static class ChatsApi
{
    public static RouteGroupBuilder MapChatsApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1").RequireAuthorization(GatewayRoles.Api);

        // Teams 認證健康：給訂閱者（relay）偵測 cookie 失效並告警
        v1.MapGet("/status", (SkypeTokenProvider tokens) => TypedResults.Ok(new AuthStatusDto(
            TeamsAuth: tokens.Healthy ? "ok" : "failed",
            LastTokenAt: tokens.LastSuccessAt,
            LastFailureAt: tokens.LastFailureAt,
            LastError: tokens.Healthy ? null : tokens.LastError,
            TokenExpiresAt: tokens.Current?.ExpiresAt)))
        .WithSummary("Teams 認證健康")
        .WithDescription("回報 gateway 與 Teams 的連線認證是否正常（relay 用來偵測登入 cookie 失效並告警）。")
        .WithTags("Status");

        v1.MapGet("/chats", async (ChatsvcClient chatsvc, CancellationToken ct) =>
            TypedResults.Ok(await chatsvc.GetConversationsAsync(ct)))
        .WithSummary("列出對話")
        .WithDescription("列出帳號的所有 Teams 對話（群組 / 1:1）。")
        .WithTags("Chats");

        v1.MapGet("/chats/{id}/messages", async Task<Results<Ok<MessagePage>, BadRequest<ApiError>>> (
            string id, ChatsvcClient chatsvc, CancellationToken ct, int? limit = null, string? cursor = null) =>
        {
            cursor = string.IsNullOrEmpty(cursor) ? null : cursor;
            if (cursor is not null && !chatsvc.IsValidCursor(id, cursor))
                return TypedResults.BadRequest(new ApiError("cursor 要帶同一個對話上一頁回的 nextCursor"));
            var page = await chatsvc.GetMessagesAsync(id, Math.Clamp(limit ?? 30, 1, 200), cursor, ct);
            return TypedResults.Ok(page);
        })
        .WithSummary("讀某對話的訊息")
        .WithDescription("即時從 chatsvc 讀。`limit` 1–200（預設 30）；`cursor` 帶上一頁回的 `nextCursor` 往更舊翻。")
        .WithTags("Chats");

        v1.MapPost("/chats/{id}/messages", async Task<Results<Ok<SendMessageResult>, BadRequest<ApiError>>> (
            string id, SendMessageRequest body, ChatsvcClient chatsvc, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
                return TypedResults.BadRequest(new ApiError("text 必填"));
            var result = await chatsvc.SendMessageAsync(id, body.Text, options.Value.DisplayName, ct);
            return TypedResults.Ok(result);
        })
        .WithSummary("發訊息到某對話")
        .WithDescription("以綁定帳號身分發一則文字訊息。")
        .WithTags("Chats");

        // 編輯歷史：目前版本 + 被取代的舊版本（只有訂閱對話、入庫後的訊息才有）
        v1.MapGet("/messages/{messageId}/revisions", async Task<Results<Ok<MessageRevisionsDto>, NotFound>> (
            string messageId, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var current = await db.MessageEvents.FindAsync([messageId], ct);
            if (current is null)
                return TypedResults.NotFound();
            var revisions = await db.MessageRevisions
                .Where(r => r.MessageId == messageId)
                .OrderBy(r => r.Version)
                .Select(r => new RevisionDto(r.Version, r.Content, r.SupersededAt))
                .ToListAsync(ct);
            return TypedResults.Ok(new MessageRevisionsDto(
                new MessageVersionDto(current.Version, current.Content, current.Edited, current.Deleted, current.UpdatedAt),
                revisions));
        })
        .WithSummary("一則訊息的編輯歷史")
        .WithDescription("`current`（目前版本）+ `revisions`（被取代的舊版本，由舊到新）。只有訂閱對話、已入庫的訊息才有。")
        .WithTags("Messages");

        v1.MapGet("/media", async Task<Results<FileContentHttpResult, BadRequest<ApiError>, ProblemHttpResult>> (
            string url, MediaClient media, CancellationToken ct) =>
        {
            if (!MediaClient.IsAllowed(url, out var uri))
                return TypedResults.BadRequest(new ApiError("只代理 Teams 媒體網址（https://*.asm.skype.com）"));
            try
            {
                var (bytes, contentType) = await media.GetAsync(uri, ct);
                return TypedResults.File(bytes, contentType);
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .WithSummary("代抓訊息內嵌圖片")
        .WithDescription("用 gateway 的 Teams 登入抓訊息裡的內嵌圖片（`<img src>` 指向 `*.asm.skype.com`，外部直接拿不到）。只接受這個網域。")
        .WithTags("Media");

        return v1;
    }
}
