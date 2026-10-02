using System.Threading.Channels;
using Microsoft.Extensions.Options;
using UofSim.Core.Messages;
using UofSim.Core.Routing;
using UofSim.Host.Amqp;

namespace UofSim.Host.Feed;

/// <param name="After">Recover messages after this timestamp (ms); null = full recovery.</param>
/// <param name="EventUrn">Set for event-level recoveries (odds / stateful messages of one event).</param>
public sealed record RecoveryRequest(int ProducerId, long RequestId, int? NodeId, long? After, string? EventUrn);

/// <summary>
/// Answers recovery requests accepted by the REST API. S1 behaviour: no snapshot replay yet - after
/// <see cref="SimOptions.RecoveryDelayMs"/> it sends <c>snapshot_complete</c> with the same request_id
/// (routed to the requesting node) and marks the producer as subscribed again.
/// </summary>
public sealed class RecoveryService(
    IFeedPublisher publisher,
    ProducerStateStore producers,
    TimeProvider clock,
    IOptions<SimOptions> options,
    ILogger<RecoveryService> log) : BackgroundService
{
    private readonly Channel<RecoveryRequest> _queue = Channel.CreateUnbounded<RecoveryRequest>();

    public void Enqueue(RecoveryRequest request) => _queue.Writer.TryWrite(request);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(options.Value.RecoveryDelayMs), clock, stoppingToken);
                await CompleteAsync(request, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Recovery {RequestId} for producer {Producer} failed", request.RequestId, request.ProducerId);
            }
        }
    }

    private async Task CompleteAsync(RecoveryRequest request, CancellationToken ct)
    {
        if (request.EventUrn is not null)
        {
            // Event-level recoveries re-send the event's messages; nothing to re-send until S2 keeps state.
            log.LogInformation("Event recovery {RequestId} for {Event} accepted (no state to re-send yet)",
                request.RequestId, request.EventUrn);
            return;
        }

        var body = FeedMessageBuilder.SnapshotComplete(
            request.ProducerId, clock.GetUtcNow().ToUnixTimeMilliseconds(), request.RequestId);
        await publisher.PublishAsync(RoutingKey.System(MessageTypes.SnapshotComplete, request.NodeId), body, ct);

        if (producers.Get(request.ProducerId).Mode == ProducerMode.Unsubscribed)
        {
            producers.Set(request.ProducerId, ProducerMode.Up);
        }
        log.LogInformation("snapshot_complete sent: producer {Producer}, request {RequestId}, node {Node}, after {After}",
            request.ProducerId, request.RequestId, request.NodeId, request.After);
    }
}
