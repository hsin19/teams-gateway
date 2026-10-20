using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Mcp;

/// <summary>
/// 把 gateway 的能力開成 MCP tool。讀 / 發就是 function，沒有需要 LLM 介入的流程，所以用 MCP 而不是 skill。
/// `chat` 參數接 thread id（19:...）或群名，gateway 端自己解析。
/// </summary>
[McpServerToolType]
public static class TeamsTools
{
    [McpServerTool(Name = "list_chats"), Description("列出 Teams 對話（群組 / 1:1）。可用 query 關鍵字篩 topic。")]
    public static async Task<IReadOnlyList<ConversationDto>> ListChats(
        ChatsvcClient chatsvc,
        [Description("關鍵字，篩選 topic（可選）")] string? query = null,
        CancellationToken ct = default)
    {
        var chats = await chatsvc.GetConversationsAsync(ct);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var low = query.ToLowerInvariant();
            chats = chats.Where(c => c.Topic?.ToLowerInvariant().Contains(low) == true).ToList();
        }
        return chats;
    }

    [McpServerTool(Name = "read_messages"), Description("讀某個 Teams 對話最近的訊息。chat 可給 thread id 或群名。")]
    public static async Task<IReadOnlyList<MessageDto>> ReadMessages(
        ChatsvcClient chatsvc,
        [Description("thread id（19:...@thread.v2）或群名")] string chat,
        [Description("最多幾則，預設 25")] int limit = 25,
        CancellationToken ct = default)
    {
        var id = await chatsvc.ResolveChatAsync(chat, ct);
        var page = await chatsvc.GetMessagesAsync(id, Math.Clamp(limit, 1, 200), cursor: null, ct);
        return page.Messages;
    }

    [McpServerTool(Name = "send_message"), Description("發一則訊息到某個 Teams 對話（以專用帳號名義、不可撤回）。chat 可給 thread id 或群名。")]
    public static async Task<SendMessageResult> SendMessage(
        ChatsvcClient chatsvc,
        IOptions<GatewayOptions> options,
        [Description("thread id（19:...@thread.v2）或群名")] string chat,
        [Description("訊息內容")] string text,
        CancellationToken ct = default)
    {
        var id = await chatsvc.ResolveChatAsync(chat, ct);
        return await chatsvc.SendMessageAsync(id, text, options.Value.DisplayName, ct);
    }
}
