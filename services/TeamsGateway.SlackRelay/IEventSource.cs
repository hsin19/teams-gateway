using System.Runtime.CompilerServices;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// relay 收事件的來源（ADR 0006）：背後是 SSE 還是 broker 由 <c>Relay:Source</c> 決定，<see cref="RelayWorker"/> 只管轉送。
/// 約定「拿下一筆 = 上一筆處理完」：source 在這時推進自己的游標（SSE 的 <c>Last-Event-ID</c>、broker 的 commit）。
/// 斷線重連、退避也在 source 裡，worker 拿到的是一條不會斷的串流。順序只保證在同一個對話內。
/// </summary>
public interface IEventSource
{
    /// <param name="chats">只收這些 Teams 對話；null = 全部。</param>
    IAsyncEnumerable<StreamEvent> ReadAsync(IReadOnlyCollection<string>? chats, CancellationToken ct);

    /// <summary>現在連著來源嗎（給 <c>/health</c>）：還沒開始讀、斷線後退避重連的空檔都是 false。</summary>
    bool Connected { get; }
}

/// <summary>
/// 從 gateway 的 SSE（<c>/v1/events</c>）收事件。游標是 <c>Last-Event-ID</c>（處理完的最後一個事件序號），斷線就帶它重連；
/// 剛啟動、還沒有序號時，從 Slack 上最後一則轉過去的 Teams 訊息之後續接（<c>afterMessage</c>，看 <see cref="MessageLinks"/>，
/// 所以要等 worker 從 Slack 回補完對應才開始讀）。重送的事件由 worker 照版本 / 序號去重。
/// </summary>
public sealed class SseEventSource(GatewayEventStream stream, MessageLinks links, ILogger<SseEventSource> logger) : IEventSource
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public bool Connected => stream.Connected;

    /// <summary>斷線後第一次重連前等多久（測試調短）。</summary>
    internal TimeSpan FirstBackoff { get; init; } = MinBackoff;

    public async IAsyncEnumerable<StreamEvent> ReadAsync(IReadOnlyCollection<string>? chats, [EnumeratorCancellation] CancellationToken ct)
    {
        var filter = chats is { Count: > 0 } ? string.Join(',', chats) : null;
        string? lastId = null;
        var resumeAfter = links.NewestTeamsId();
        var backoff = FirstBackoff;

        while (!ct.IsCancellationRequested)
        {
            await using (var events = stream.ConnectAsync(filter, lastId, resumeAfter, ct).GetAsyncEnumerator(ct))
            {
                while (true)
                {
                    StreamEvent evt;
                    try
                    {
                        if (!await events.MoveNextAsync())
                            break; // 對方關掉連線：照樣退避後重連
                        evt = events.Current;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        yield break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "gateway SSE 連線中斷，{Seconds}s 後重連", backoff.TotalSeconds);
                        break;
                    }
                    backoff = FirstBackoff; // 收得到就重置退避
                    yield return evt;
                    lastId = evt.Id ?? lastId; // 呼叫端來拿下一筆 = 這筆處理完（或放棄）了，才往前推
                }
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }
}
