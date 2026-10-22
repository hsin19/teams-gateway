using System.Diagnostics.Metrics;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway;

/// <summary>訊息是從哪條路進來的。</summary>
public enum IngestSource { Trouter, Poll, Reconcile, Backfill }

/// <summary>
/// gateway 自訂的 metrics（meter <see cref="MeterName"/>）。有設 <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> 就隨 OpenTelemetry 匯出；
/// 沒有後端時用 <c>dotnet-counters monitor -n TeamsGateway.Gateway --counters TeamsGateway.Gateway</c> 直接看行程內的值。
/// 主要看推播可靠度：別人的新訊息有多少是 Trouter 即時推到、多少要靠輪詢 / 慢更新 / 啟動回補才補到。
/// </summary>
public sealed class GatewayMetrics
{
    public const string MeterName = "TeamsGateway.Gateway";

    private readonly SkypeTokenProvider _tokens;
    private readonly Counter<long> _delivered;
    private readonly Histogram<double> _pushLatency;

    public GatewayMetrics(IMeterFactory meterFactory, SkypeTokenProvider tokens)
    {
        _tokens = tokens;
        var meter = meterFactory.Create(MeterName);
        _delivered = meter.CreateCounter<long>(
            "teams_gateway.messages.delivered", unit: "{message}",
            description: "發給訂閱者的新訊息 / 編輯，依來源（trouter / poll / reconcile / backfill）、變更（new / edit）、發話者（self / others）分");
        _pushLatency = meter.CreateHistogram(
            "teams_gateway.push.latency", unit: "s",
            description: "Trouter 推到的新訊息從發送到收到的延遲",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.25, 0.5, 1, 2, 5, 10, 30, 60] });
    }

    /// <summary>一則訊息入庫、發給訂閱者之後呼叫；沒有變化（重送）不算。</summary>
    public void Delivered(IngestSource source, IngestResult result, MessageDto message)
    {
        if (result is IngestResult.Unchanged)
            return;
        // 自己帳號發的訊息不管註冊對不對都會推來，要跟別人的分開看才看得出推播有沒有壞
        var sender = _tokens.Current is { } token && message.From == $"8:{token.SkypeId}" ? "self" : "others";
        _delivered.Add(1,
            new KeyValuePair<string, object?>("source", source.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>("change", result is IngestResult.Inserted ? "new" : "edit"),
            new KeyValuePair<string, object?>("sender", sender));
        if (source is IngestSource.Trouter && result is IngestResult.Inserted)
            _pushLatency.Record(Math.Max(0, (DateTimeOffset.UtcNow - message.Time).TotalSeconds),
                new KeyValuePair<string, object?>("sender", sender));
    }
}
