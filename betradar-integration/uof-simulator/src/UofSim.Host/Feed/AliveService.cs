using Microsoft.Extensions.Options;
using UofSim.Core.Messages;
using UofSim.Core.Routing;
using UofSim.Host.Amqp;

namespace UofSim.Host.Feed;

/// <summary>Publishes <c>alive</c> for every producer each <see cref="SimOptions.AliveIntervalSeconds"/>.</summary>
public sealed class AliveService(
    IFeedPublisher publisher,
    ProducerStateStore producers,
    TimeProvider clock,
    IOptions<SimOptions> options,
    ILogger<AliveService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.AliveIntervalSeconds), clock);
        do
        {
            await TickAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var state in producers.All)
        {
            if (state.Mode == ProducerMode.Silent)
            {
                continue;
            }
            var body = FeedMessageBuilder.Alive(state.ProducerId, now, subscribed: state.Mode == ProducerMode.Up);
            try
            {
                await publisher.PublishAsync(RoutingKey.System(MessageTypes.Alive), body, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "alive for producer {Producer} not published", state.ProducerId);
            }
        }
    }
}
