using Microsoft.EntityFrameworkCore;
using TeamsGateway.Service.Data;
using TeamsGateway.Service.Events;

namespace TeamsGateway.Service.Teams;

/// <summary>常駐跑 Trouter 連線：收到新訊息就去重寫進 DB，再 publish 給 SSE / webhook。斷線重連，退避。</summary>
public sealed class TrouterBackgroundService(
    TrouterClient trouter,
    IDbContextFactory<GatewayDbContext> factory,
    GatewayEventBus bus,
    ILogger<TrouterBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = MinBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await trouter.RunAsync(OnMessageAsync, stoppingToken);
                backoff = MinBackoff; // 正常斷線，重連從頭退避
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 常見是還沒匯入 cookie（換不到 token），退避後再試
                logger.LogWarning(ex, "Trouter 連線中斷，{Backoff} 後重連", backoff);
                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    private async Task OnMessageAsync(IncomingMessage incoming, CancellationToken ct)
    {
        var dto = incoming.Message;
        await using var db = await factory.CreateDbContextAsync(ct);

        // 以 Teams 訊息 id 當主鍵天然去重（自己發的也會推回來、Trouter 偶爾重送）
        if (await db.MessageEvents.AnyAsync(m => m.Id == dto.Id, ct))
            return;

        db.MessageEvents.Add(new MessageEvent
        {
            Id = dto.Id,
            ConversationId = dto.ConversationId,
            SenderMri = dto.From,
            SenderName = dto.FromName,
            MessageType = dto.Type,
            Content = dto.Content,
            SequenceId = dto.SequenceId,
            ComposeTime = dto.Time,
            ReceivedAt = DateTimeOffset.UtcNow,
            RawJson = incoming.RawJson,
        });

        var conversation = await db.Conversations.FindAsync([dto.ConversationId], ct);
        if (conversation is null)
        {
            db.Conversations.Add(new Conversation
            {
                Id = dto.ConversationId,
                Topic = incoming.ThreadTopic,
                LastMessageId = dto.Id,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            conversation.Topic = incoming.ThreadTopic ?? conversation.Topic;
            conversation.LastMessageId = dto.Id;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        bus.Publish(dto);
    }
}
