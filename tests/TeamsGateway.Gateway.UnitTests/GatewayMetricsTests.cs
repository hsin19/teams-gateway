using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class GatewayMetricsTests
{
    private static MessageDto Msg(DateTimeOffset time) =>
        new("1", "19:x@thread.v2", "8:live:someone", "Someone", "Text", "hi", 1, time);

    [Fact]
    public void Delivered_records_source_change_sender_and_trouter_latency()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        using var db = new TestDb();
        var tokens = new SkypeTokenProvider(new SecureStore(db, new EphemeralDataProtectionProvider()), NullLogger<SkypeTokenProvider>.Instance);
        var metrics = new GatewayMetrics(meterFactory, tokens);

        var delivered = new List<Dictionary<string, object?>>();
        var latencies = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, meterFactory))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            delivered.Add(tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => latencies.Add(value));
        listener.Start();

        metrics.Delivered(IngestSource.Trouter, IngestResult.Inserted, Msg(DateTimeOffset.UtcNow.AddSeconds(-2)));
        metrics.Delivered(IngestSource.Poll, IngestResult.Updated, Msg(DateTimeOffset.UtcNow));
        metrics.Delivered(IngestSource.Poll, IngestResult.Unchanged, Msg(DateTimeOffset.UtcNow)); // 重送不算

        Assert.Equal(2, delivered.Count);
        Assert.Equal(("trouter", "new", "others"), (delivered[0]["source"], delivered[0]["change"], delivered[0]["sender"]));
        Assert.Equal(("poll", "edit", "others"), (delivered[1]["source"], delivered[1]["change"], delivered[1]["sender"]));

        var latency = Assert.Single(latencies); // 只有 Trouter 推到的新訊息記延遲
        Assert.InRange(latency, 1.5, 30);
    }
}
