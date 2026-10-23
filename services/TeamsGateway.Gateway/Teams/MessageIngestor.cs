using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.Teams;

public enum IngestResult { Inserted, Updated, Unchanged }

/// <summary>
/// 把一則訊息 upsert 進 DB（Trouter 即時、回補、慢更新共用）。
/// 以 Teams 訊息 id 為主鍵；同 id 但 version 更大 = 被編輯 / 刪除 → 更新內容與標記。live 才 publish。
/// publish 的事件跟訊息同一個交易記進內部 log（<see cref="PublishedEvent"/>），commit 之後才通知 bus 有新事件，
/// SSE 與各 sink 都從 log 讀（ADR 0006）。版本變大時也比對表情，有人按 / 收回就發 reaction 事件；
/// 新訊息進來時已經掛著的表情，接在訊息後面當成新按的發。
/// </summary>
public sealed class MessageIngestor(IDbContextFactory<GatewayDbContext> factory, GatewayEventBus bus, IPeopleDirectory? people = null)
{
    // Trouter、輪詢、慢更新、回補會同時寫同一則（先查再插 → 主鍵衝突、編輯的 revision 重複）：寫入一次一筆。
    // 量很小，串行化沒有成本。也保證事件序號 = commit 順序（ADR 0006 的讀法靠它）：Postgres 的序號在 INSERT 時就分配，
    // 兩筆交易同時跑時序號小的可能比較晚 commit，讀游標的人會跳過它——所以寫 PublishedEvents 只能經過這把鎖（ADR 0008）
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<bool> IsSubscribedAsync(string conversationId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Conversations.AnyAsync(c => c.Id == conversationId && c.Subscribed, ct);
    }

    public async Task<IngestResult> UpsertAsync(MessageDto dto, string rawJson, bool publish, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            return await UpsertCoreAsync(dto, rawJson, publish, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<IngestResult> UpsertCoreAsync(MessageDto dto, string rawJson, bool publish, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.MessageEvents.FindAsync([dto.Id], ct);
        var outgoing = new List<PublishedEvent>();

        IngestResult result;
        if (existing is null)
        {
            db.MessageEvents.Add(new MessageEvent
            {
                Id = dto.Id,
                ConversationId = dto.ConversationId,
                SenderMri = dto.From,
                SenderName = dto.FromName,
                MessageType = dto.Type,
                Content = dto.Content,
                SequenceId = dto.SequenceId,
                Version = dto.Version,
                Edited = dto.Edited,
                Deleted = dto.Deleted,
                ComposeTime = dto.Time,
                ReceivedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                RawJson = rawJson,
            });
            if (publish)
            {
                var conv = await db.Conversations.FindAsync([dto.ConversationId], ct);
                if (conv is not null)
                {
                    conv.LastMessageId = dto.Id;
                    conv.UpdatedAt = DateTimeOffset.UtcNow;
                }
                outgoing.Add(Record(db, new GatewayEvent(Message: dto), rawJson));
                // 晚到的訊息（睡眠 / 停機後才被輪詢、回補補到）第一次看到時可能已經有人按了表情：
                // 之後的比對都以這版為準，現在不發就永遠發不出去
                foreach (var reaction in await ReactionChangesAsync(db, beforeJson: "{}", rawJson, dto, ct))
                    outgoing.Add(Record(db, new GatewayEvent(Reaction: reaction), rawJson));
            }
            result = IngestResult.Inserted;
        }
        else if (dto.Version <= existing.Version)
        {
            return IngestResult.Unchanged; // 同版或更舊（重送）
        }
        else
        {
            // 表情：比對前後兩版誰按了 / 收回了什麼（有人按表情時 Teams 也會讓 version 往上跳）
            var reactions = publish ? await ReactionChangesAsync(db, existing.RawJson, rawJson, dto, ct) : [];

            if (SameVisible(existing, dto))
            {
                // 看得到的都沒變（例如只是有人按表情）：只把版本 / 原始 JSON 跟上（之後的比對才準），
                // 不留 revision、不當成編輯發出去——不然 Slack 會多出一則一模一樣的「編輯」
                existing.Version = dto.Version;
                existing.Edited = dto.Edited;
                existing.RawJson = rawJson;
                result = IngestResult.Unchanged;
            }
            else
            {
                // 被編輯 / 刪除：先留存舊版本，再更新成最新
                db.MessageRevisions.Add(new MessageRevision
                {
                    MessageId = existing.Id,
                    Version = existing.Version,
                    Content = existing.Content,
                    RawJson = existing.RawJson,
                    SupersededAt = DateTimeOffset.UtcNow,
                });
                existing.Content = dto.Content;
                existing.MessageType = dto.Type;
                existing.Version = dto.Version;
                existing.Edited = dto.Edited;
                existing.Deleted = dto.Deleted;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.RawJson = rawJson;
                if (publish)
                    outgoing.Add(Record(db, new GatewayEvent(Message: dto), rawJson));
                result = IngestResult.Updated;
            }
            foreach (var reaction in reactions)
                outgoing.Add(Record(db, new GatewayEvent(Reaction: reaction), rawJson));
        }

        await db.SaveChangesAsync(ct);
        if (outgoing.Count > 0)
            bus.Notify(outgoing[^1].Seq);
        return result;
    }

    /// <summary>看得到的部分（內容、類型、刪除、附件）一樣。</summary>
    private static bool SameVisible(MessageEvent stored, MessageDto incoming) =>
        stored.Content == incoming.Content
        && stored.MessageType == incoming.Type
        && stored.Deleted == incoming.Deleted
        && (ChatsvcClient.ParseAttachments(stored.RawJson) ?? []).SequenceEqual(incoming.Attachments ?? []);

    /// <summary>
    /// 前後兩版的表情變動。按的人的名字先用他在 DB 裡最近一則訊息的顯示名稱；沒在訂閱群組講過話的（只按表情）
    /// 再去 Teams 查（<see cref="IPeopleDirectory"/>），都查不到就 null。
    /// </summary>
    private async Task<List<ReactionDto>> ReactionChangesAsync(
        GatewayDbContext db, string beforeJson, string afterJson, MessageDto dto, CancellationToken ct)
    {
        var changes = DiffReactions(ChatsvcClient.ParseReactions(beforeJson), ChatsvcClient.ParseReactions(afterJson), DateTimeOffset.UtcNow);
        if (changes.Count == 0)
            return [];

        var names = new Dictionary<string, string?>();
        foreach (var mri in changes.Select(c => c.Mri).Distinct())
            names[mri] = await db.MessageEvents
                .Where(m => m.SenderMri == mri && m.SenderName != null)
                .OrderByDescending(m => m.ReceivedAt)
                .Select(m => m.SenderName)
                .FirstOrDefaultAsync(ct);
        var unknown = names.Where(n => n.Value is null).Select(n => n.Key).ToList();
        if (unknown.Count > 0 && people is not null)
            foreach (var (mri, name) in await people.DisplayNamesAsync(unknown, ct))
                names[mri] = name;

        return changes
            .Select(c => new ReactionDto(dto.Id, dto.ConversationId, c.Emoji, c.Mri, names[c.Mri], c.Added, c.Time, c.Count))
            .ToList();
    }

    internal readonly record struct ReactionChange(string Emoji, string Mri, bool Added, DateTimeOffset Time, int Count);

    /// <summary>
    /// 誰新按了、誰收回了哪個表情，照時間排。按下用 Teams 記的時間；收回 Teams 不留紀錄，用 <paramref name="now"/>。
    /// <see cref="ReactionChange.Count"/> 是那個表情變動後還有幾個人按著。
    /// </summary>
    internal static List<ReactionChange> DiffReactions(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> before,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> after,
        DateTimeOffset now)
    {
        var none = new Dictionary<string, DateTimeOffset>();
        var changes = new List<ReactionChange>();
        foreach (var emoji in before.Keys.Union(after.Keys))
        {
            var was = before.GetValueOrDefault(emoji) ?? none;
            var isNow = after.GetValueOrDefault(emoji) ?? none;
            foreach (var (mri, time) in isNow)
                if (!was.ContainsKey(mri))
                    changes.Add(new ReactionChange(emoji, mri, Added: true, time, isNow.Count));
            foreach (var mri in was.Keys)
                if (!isNow.ContainsKey(mri))
                    changes.Add(new ReactionChange(emoji, mri, Added: false, now, isNow.Count));
        }
        return changes.OrderBy(c => c.Time).ThenBy(c => c.Emoji, StringComparer.Ordinal).ToList();
    }

    /// <summary>把要發的事件加進內部 log（跟訊息同一個交易）；SaveChanges 之後 <see cref="PublishedEvent.Seq"/> 才有值。</summary>
    private static PublishedEvent Record(GatewayDbContext db, GatewayEvent evt, string rawJson)
    {
        var row = new PublishedEvent
        {
            Kind = evt.Kind,
            MessageId = evt.MessageId,
            ConversationId = evt.ConversationId,
            Payload = EventLog.Payload(evt, rawJson),
            SchemaVersion = EventSchema.Version,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        db.PublishedEvents.Add(row);
        return row;
    }
}
