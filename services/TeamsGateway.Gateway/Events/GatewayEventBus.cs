using System.Text.Json;
using System.Threading.Channels;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.Events;

/// <summary>要記進內部 log（<c>PublishedEvents</c>）的一個事件：一則訊息（新 / 編輯 / 刪除）或一次表情變動，兩者擇一。</summary>
public sealed record GatewayEvent(MessageDto? Message = null, ReactionDto? Reaction = null)
{
    /// <summary>SSE 的 event 名稱 / webhook header（見 <see cref="EventKinds"/>）。</summary>
    public string Kind => Reaction is null ? EventKinds.Message : EventKinds.Reaction;

    public string MessageId => Message?.Id ?? Reaction!.MessageId;

    public string ConversationId => Message?.ConversationId ?? Reaction!.ConversationId;

    public string ToJson() => Reaction is null
        ? JsonSerializer.Serialize(Message!, ContractsJsonContext.Default.MessageDto)
        : JsonSerializer.Serialize(Reaction, ContractsJsonContext.Default.ReactionDto);
}

/// <summary>
/// 行程內「有新事件」的通知（ADR 0006）：<c>MessageIngestor</c> 把事件寫進內部 log、commit 之後 <see cref="Notify"/>，
/// SSE 連線與各 sink 被叫醒後，自己從 log 讀游標之後的事件。通知會合併、也可能在訂閱前就發過，
/// 都不會漏事件：內容一律從 log 讀，通知只負責「該去讀了」。
/// </summary>
public sealed class GatewayEventBus
{
    private readonly Lock _lock = new();
    private readonly List<Channel<long>> _subscribers = [];

    public Subscription Subscribe()
    {
        // 容量 1、丟舊的：還沒醒的訂閱者只需要知道「有新的」，通知幾次都一樣
        var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_lock)
            _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    /// <summary>序號到 <paramref name="seq"/> 為止的事件都已經寫進 log。</summary>
    public void Notify(long seq)
    {
        lock (_lock)
            foreach (var channel in _subscribers)
                channel.Writer.TryWrite(seq);
    }

    private void Remove(Channel<long> channel)
    {
        lock (_lock)
            _subscribers.Remove(channel);
    }

    public sealed class Subscription(GatewayEventBus bus, Channel<long> channel) : IDisposable
    {
        /// <summary>收到的通知（值是當時最新的序號）；平常用 <see cref="WaitAsync"/>，測試用它看有沒有被通知。</summary>
        public ChannelReader<long> Reader => channel.Reader;

        /// <summary>等到有新事件（true）或過了 <paramref name="timeout"/>（false）。</summary>
        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout);
            try
            {
                if (!await channel.Reader.WaitToReadAsync(wait.Token))
                    return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            while (channel.Reader.TryRead(out _))
            {
            }
            return true;
        }

        public void Dispose()
        {
            bus.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
