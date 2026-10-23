namespace TeamsGateway.Gateway.Events;

/// <summary>
/// 事件出口（ADR 0006）：從自己的游標往後讀內部 log → 送出 → 成功才推進游標。
/// 開哪些由 <c>Gateway:Sinks:&lt;名稱&gt;:Enabled</c> 決定（預設開）。SSE 的游標在 client，不是 sink。
/// </summary>
public interface IEventSink
{
    /// <summary>設定裡的名稱，例如 <c>Webhook</c>。</summary>
    string Name { get; }

    /// <summary>還有人沒送到的最小序號：pruner 不清它和之後的事件，下游掛幾天也不掉。沒有要等的回 null。</summary>
    Task<long?> OldestPendingAsync(CancellationToken ct);
}
