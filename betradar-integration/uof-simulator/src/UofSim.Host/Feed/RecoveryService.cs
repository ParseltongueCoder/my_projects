using System.Threading.Channels;
using Microsoft.Extensions.Options;
using UofSim.Core.Messages;
using UofSim.Core.Recovery;
using UofSim.Core.Routing;
using UofSim.Host.Amqp;

namespace UofSim.Host.Feed;

/// <param name="After">Recover messages after this timestamp (ms); null = full recovery.</param>
/// <param name="EventUrn">Set for event-level recoveries (odds / stateful messages of one event).</param>
public sealed record RecoveryRequest(int ProducerId, long RequestId, int? NodeId, long? After, string? EventUrn);

/// <summary>
/// Answers recovery requests accepted by the REST API: after <see cref="SimOptions.RecoveryDelayMs"/> it
/// re-sends the producer's current odds per event (<see cref="SnapshotStore"/>, stamped with the request_id),
/// then <c>snapshot_complete</c> with the same request_id routed to the requesting node, and marks the
/// producer as subscribed again. Event recoveries do the same for one event.
/// </summary>
public sealed class RecoveryService(
    IFeedPublisher publisher,
    ProducerStateStore producers,
    SnapshotStore snapshots,
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
        var snapshot = snapshots.ForProducer(request.ProducerId, request.EventUrn);
        foreach (var message in snapshot)
        {
            var body = FeedMessageBuilder.Restamp(message.Body, clock.GetUtcNow().ToUnixTimeMilliseconds(), request.RequestId);
            await publisher.PublishAsync(message.RoutingKey, body, ct);
        }

        var complete = FeedMessageBuilder.SnapshotComplete(
            request.ProducerId, clock.GetUtcNow().ToUnixTimeMilliseconds(), request.RequestId);
        await publisher.PublishAsync(RoutingKey.System(MessageTypes.SnapshotComplete, request.NodeId), complete, ct);

        if (request.EventUrn is null && producers.Get(request.ProducerId).Mode == ProducerMode.Unsubscribed)
        {
            producers.Set(request.ProducerId, ProducerMode.Up);
        }
        log.LogInformation(
            "Recovery {RequestId} answered: producer {Producer}, event {Event}, {Count} snapshot messages, node {Node}, after {After}",
            request.RequestId, request.ProducerId, request.EventUrn ?? "*", snapshot.Count, request.NodeId, request.After);
    }
}
