using System.Text.Json;
using System.Threading.Channels;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.Events;

/// <summary>
/// 發給訂閱者的一個事件：一則訊息（新 / 編輯 / 刪除）或一次表情變動，兩者擇一。
/// <see cref="Seq"/> 對應 <c>PublishedEvents</c> 表，也是 SSE 的 event id。
/// </summary>
public sealed record GatewayEvent(long Seq, MessageDto? Message = null, ReactionDto? Reaction = null)
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
/// 行程內的新訊息 pub/sub。Trouter 背景工作 publish，SSE 連線與 webhook 投遞各自 subscribe。
/// 單一行程夠用；要拆多實例時再換成真的 broker（見 ADR 0002）。
/// </summary>
public sealed class GatewayEventBus
{
    private readonly Lock _lock = new();
    private readonly List<Channel<GatewayEvent>> _subscribers = [];

    public Subscription Subscribe()
    {
        // 慢的訂閱者滿了就丟最舊的，不要卡住 publish（SSE 看得出序號斷洞，會從 DB 補回）
        var channel = Channel.CreateBounded<GatewayEvent>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_lock)
            _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    public void Publish(GatewayEvent evt)
    {
        lock (_lock)
            foreach (var channel in _subscribers)
                channel.Writer.TryWrite(evt);
    }

    private void Remove(Channel<GatewayEvent> channel)
    {
        lock (_lock)
            _subscribers.Remove(channel);
    }

    public sealed class Subscription(GatewayEventBus bus, Channel<GatewayEvent> channel) : IDisposable
    {
        public ChannelReader<GatewayEvent> Reader => channel.Reader;

        public void Dispose()
        {
            bus.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
