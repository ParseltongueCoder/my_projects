using UofSim.Core.Recovery;
using UofSim.Core.SportsApi;
using UofSim.Host.Feed;

namespace UofSim.Host.Amqp;

/// <summary>
/// Publishes to the transport, then lets observers see the message: the event state behind the
/// Sports API summaries, the recovery snapshots and the optional recorder.
/// </summary>
public sealed class FeedPipeline(IFeedTransport transport, EventStateStore states, SnapshotStore snapshots, FeedRecorder recorder)
    : IFeedPublisher
{
    public async Task PublishAsync(string routingKey, byte[] body, CancellationToken ct = default)
    {
        await transport.PublishAsync(routingKey, body, ct);
        states.Observe(body);
        snapshots.Observe(routingKey, body);
        recorder.Observe(routingKey, body);
    }
}
