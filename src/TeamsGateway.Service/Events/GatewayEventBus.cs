using System.Threading.Channels;
using TeamsGateway.Service.Model;

namespace TeamsGateway.Service.Events;

/// <summary>
/// 行程內的新訊息 pub/sub。Trouter 背景工作 publish，SSE 連線與 webhook 投遞各自 subscribe。
/// 單一行程夠用；要拆多實例時再換成真的 broker（見 ADR 0002）。
/// </summary>
public sealed class GatewayEventBus
{
    private readonly Lock _lock = new();
    private readonly List<Channel<MessageDto>> _subscribers = [];

    public Subscription Subscribe()
    {
        // 慢的訂閱者滿了就丟最舊的，不要卡住 publish
        var channel = Channel.CreateBounded<MessageDto>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_lock)
            _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    public void Publish(MessageDto message)
    {
        lock (_lock)
            foreach (var channel in _subscribers)
                channel.Writer.TryWrite(message);
    }

    private void Remove(Channel<MessageDto> channel)
    {
        lock (_lock)
            _subscribers.Remove(channel);
    }

    public sealed class Subscription(GatewayEventBus bus, Channel<MessageDto> channel) : IDisposable
    {
        public ChannelReader<MessageDto> Reader => channel.Reader;

        public void Dispose()
        {
            bus.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
