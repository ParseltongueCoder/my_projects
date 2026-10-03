using System.Diagnostics.Metrics;

namespace Uof.Adapter;

/// <summary>
/// Feed-health metrics (docs/04 §5.2), exported at <c>/metrics</c> in Prometheus format. Prometheus names:
/// <c>uof_messages_total</c>, <c>uof_message_lag_seconds</c>, <c>uof_store_duration_seconds</c>,
/// <c>uof_queue_depth</c>, <c>uof_producer_up</c>, <c>uof_producer_down_suspended_markets_total</c>.
/// </summary>
public sealed class AdapterMetrics : IDisposable
{
    public const string MeterName = "Uof.Adapter";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _messages;
    private readonly Histogram<double> _lag;
    private readonly Histogram<double> _storeDuration;
    private readonly Counter<long> _suspendedByProducerDown;
    private readonly Dictionary<int, int> _producerUp = [];
    private readonly object _gate = new();

    /// <summary>Set by the feed service once its queue exists.</summary>
    public Func<int> QueueDepth { get; set; } = () => 0;

    public AdapterMetrics()
    {
        _messages = _meter.CreateCounter<long>("uof.messages", description: "Feed messages by type and outcome (processed, duplicate, failed)");
        _lag = _meter.CreateHistogram<double>("uof.message.lag", "s",
            "From the message's feed timestamp to it being stored", advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30],
            });
        _storeDuration = _meter.CreateHistogram<double>("uof.store.duration", "s",
            "Time to archive and apply one message", advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 1],
            });
        _suspendedByProducerDown = _meter.CreateCounter<long>("uof.producer_down.suspended_markets",
            description: "Markets suspended because their producer went down");
        _meter.CreateObservableGauge("uof.queue.depth", () => QueueDepth(), description: "Messages waiting to be stored");
        _meter.CreateObservableGauge("uof.producer.up", ObserveProducers, description: "1 if the producer is up, 0 if down");
    }

    public void Message(string type, string outcome, TimeSpan lag, TimeSpan storeDuration)
    {
        var tags = new KeyValuePair<string, object?>[] { new("type", type), new("outcome", outcome) };
        _messages.Add(1, tags);
        _lag.Record(Math.Max(0, lag.TotalSeconds), new KeyValuePair<string, object?>("type", type));
        _storeDuration.Record(storeDuration.TotalSeconds, new KeyValuePair<string, object?>("type", type));
    }

    public void ProducerState(int producerId, bool up, int suspendedMarkets = 0)
    {
        lock (_gate)
        {
            _producerUp[producerId] = up ? 1 : 0;
        }
        if (suspendedMarkets > 0)
        {
            _suspendedByProducerDown.Add(suspendedMarkets, new KeyValuePair<string, object?>("producer", producerId));
        }
    }

    private IEnumerable<Measurement<int>> ObserveProducers()
    {
        lock (_gate)
        {
            return _producerUp.Select(p => new Measurement<int>(p.Value, new KeyValuePair<string, object?>("producer", p.Key))).ToList();
        }
    }

    public void Dispose() => _meter.Dispose();
}
