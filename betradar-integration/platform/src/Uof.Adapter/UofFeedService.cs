using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Canonical.Db;
using Platform.Canonical.Feed;
using Platform.Canonical.Store;
using Sportradar.OddsFeed.SDK.Api;
using Sportradar.OddsFeed.SDK.Api.Config;
using Sportradar.OddsFeed.SDK.Api.EventArguments;
using Sportradar.OddsFeed.SDK.Entities;
using Sportradar.OddsFeed.SDK.Entities.Rest;
using Sportradar.OddsFeed.SDK.Entities.Rest.Market;

namespace Uof.Adapter;

/// <summary>
/// The UOF adapter: the official SDK owns the AMQP session, alive tracking and recovery; every event
/// message's raw XML is queued and applied in arrival order to the canonical store.
/// </summary>
public sealed class UofFeedService(
    IServiceProvider services,
    NpgsqlDataSource db,
    CanonicalStore store,
    AdapterMetrics metrics,
    IOptions<UofOptions> options,
    ILogger<UofFeedService> log) : BackgroundService
{
    // Feed messages and producer state changes share one queue so they are applied in arrival order:
    // messages received before a producer went down are stored before its markets are suspended.
    private abstract record Work;

    private sealed record FeedWork(string RawXml, ISportEvent Event, DateTimeOffset? SentTs) : Work;

    private sealed record ProducerWork(int ProducerId, string Name, bool Up, DateTimeOffset At) : Work;

    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, long> _eventIds = new();
    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo(options.Value.Language);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        metrics.QueueDepth = () => _queue.Reader.Count;
        var applied = await MigrationRunner.MigrateAsync(db, stoppingToken);
        log.LogInformation("Database migrated ({Count} new: {Versions})", applied.Count, string.Join(", ", applied));

        var sdk = new UofSdk(services);
        var session = sdk.GetSessionBuilder().SetMessageInterest(MessageInterest.AllMessages).Build();

        session.OnOddsChange += (_, e) => Enqueue(e.GetOddsChange(_culture));
        session.OnBetStop += (_, e) => Enqueue(e.GetBetStop(_culture));
        session.OnBetSettlement += (_, e) => Enqueue(e.GetBetSettlement(_culture));
        session.OnRollbackBetSettlement += (_, e) => Enqueue(e.GetBetSettlementRollback(_culture));
        session.OnBetCancel += (_, e) => Enqueue(e.GetBetCancel(_culture));
        session.OnRollbackBetCancel += (_, e) => Enqueue(e.GetBetCancelRollback(_culture));
        session.OnFixtureChange += (_, e) => Enqueue(e.GetFixtureChange(_culture));
        session.OnUnparsableMessageReceived += (_, e) => log.LogWarning("Unparsable {Type} message", e.MessageType);

        sdk.ProducerUp += (_, e) => ProducerChanged(e, up: true);
        sdk.ProducerDown += (_, e) => ProducerChanged(e, up: false);
        sdk.RecoveryInitiated += (_, e) => log.LogInformation("Recovery {RequestId} initiated for producer {Producer}",
            e.GetRequestId(), e.GetProducerId());

        sdk.Open();
        log.LogInformation("UOF session open ({Environment})", options.Value.Environment);

        await SeedMarketDescriptionsAsync(sdk, stoppingToken);

        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(work, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sdk.Close();
        }
    }

    private void Enqueue<T>(IEventMessage<T> message) where T : ISportEvent =>
        _queue.Writer.TryWrite(new FeedWork(
            Encoding.UTF8.GetString(message.RawMessage),
            message.Event,
            message.Timestamps?.Sent is > 0 and var sent ? DateTimeOffset.FromUnixTimeMilliseconds(sent) : null));

    private async Task ProcessAsync(Work work, CancellationToken ct)
    {
        switch (work)
        {
            case FeedWork feed:
                await ProcessFeedAsync(feed, ct);
                break;
            case ProducerWork producer:
                await ProcessProducerAsync(producer, ct);
                break;
        }
    }

    private async Task ProcessProducerAsync(ProducerWork p, CancellationToken ct)
    {
        try
        {
            await store.SetProducerStateAsync(p.ProducerId, p.Name, p.Up ? "up" : "down", p.Up ? null : "producer_down", ct);
            var suspended = 0;
            if (p.Up)
            {
                var reopened = await store.ReopenProducerMarketsAsync(p.ProducerId, ct);
                log.LogInformation("Producer {Id} ({Name}) up: {Count} markets re-opened", p.ProducerId, p.Name, reopened);
            }
            else
            {
                suspended = await store.SuspendProducerMarketsAsync(p.ProducerId, p.At, ct);
                log.LogWarning("Producer {Id} ({Name}) down: {Count} markets suspended", p.ProducerId, p.Name, suspended);
            }
            metrics.ProducerState(p.ProducerId, p.Up, suspended);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Producer {Id} state change not stored", p.ProducerId);
        }
    }

    private async Task ProcessFeedAsync(FeedWork work, CancellationToken ct)
    {
        try
        {
            var command = UofFeedParser.Parse(work.RawXml);
            var eventId = await EventIdAsync(work.Event, ct);
            var started = TimeProvider.System.GetTimestamp();
            var outcome = await store.ApplyAsync(command, eventId, work.RawXml, sentTs: work.SentTs, ct: ct);
            metrics.Message(command.MessageType, outcome.ToString().ToLowerInvariant(),
                DateTimeOffset.UtcNow - command.FeedTs, TimeProvider.System.GetElapsedTime(started));
            log.Log(outcome == ApplyOutcome.Failed ? LogLevel.Error : LogLevel.Debug,
                "{Type} {Event} → {Outcome}", command.MessageType, command.EventUrn, outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep consuming: one bad message must not stop the feed. The failure is visible in the logs/metrics.
            log.LogError(ex, "Failed to process message for {Event}", work.Event.Id);
            metrics.Message("unknown", "error", TimeSpan.Zero, TimeSpan.Zero);
        }
    }

    private async Task<long> EventIdAsync(ISportEvent ev, CancellationToken ct)
    {
        var urn = ev.Id.ToString();
        if (_eventIds.TryGetValue(urn, out var id))
        {
            return id;
        }
        id = await store.FindEventIdAsync(urn, ct)
             ?? await store.EnsureEventAsync(await EventResolver.ResolveAsync(ev, _culture), ct);
        _eventIds[urn] = id;
        return id;
    }

    private async Task SeedMarketDescriptionsAsync(IUofSdk sdk, CancellationToken ct)
    {
        try
        {
            // Right after Open() the SDK may still be loading its description cache and returns an empty list.
            var descriptions = new List<IMarketDescription>();
            for (var attempt = 0; attempt < 15 && descriptions.Count == 0; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                descriptions = (await sdk.MarketDescriptionManager.GetMarketDescriptionsAsync(_culture) ?? []).ToList();
            }
            await store.SeedMarketDescriptionsAsync(descriptions.Select(d => new MarketDescriptionRef(
                (int)d.Id,
                d.GetName(_culture) ?? $"Market {d.Id}",
                d.Groups?.ToList() ?? [],
                d.Specifiers?.Select(s => new SpecifierRef(s.Name, s.Type)).ToList() ?? [],
                d.Outcomes?.Select(o => new OutcomeDescriptionRef(o.Id, o.GetName(_culture) ?? o.Id)).ToList() ?? [])), ct);
            log.LogInformation("Seeded {Count} market descriptions", descriptions.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Market descriptions not seeded; unknown markets get placeholders");
        }
    }

    private void ProducerChanged(ProducerStatusChangeEventArgs e, bool up)
    {
        var producer = e.GetProducerStatusChange().Producer;
        log.LogInformation("Producer {Id} ({Name}) {State}", producer.Id, producer.Name, up ? "up" : "down");
        _queue.Writer.TryWrite(new ProducerWork(producer.Id, producer.Name, up, DateTimeOffset.UtcNow));
    }
}
