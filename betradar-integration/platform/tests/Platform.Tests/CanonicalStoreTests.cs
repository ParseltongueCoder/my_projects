using Dapper;
using Platform.Canonical.Feed;
using Platform.Canonical.Store;
using UofSim.Core.Messages;
using UofSim.Core.Scenarios;

namespace Platform.Tests;

/// <summary>Drives simulator scenarios through the parser into a real PostgreSQL with the canonical schema.</summary>
public class CanonicalStoreTests : IAsyncLifetime
{
    private const string EventUrn = "sr:match:900000002";
    private readonly TestDatabase _db = new();
    private CanonicalStore _store = null!;
    private long _eventId;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (TestDatabase.ServerConnectionString is null)
        {
            return;
        }
        _store = new CanonicalStore(_db.DataSource);
        await _store.SeedMarketDescriptionsAsync([
            new MarketDescriptionRef(1, "1x2", ["all", "score", "regular_play"], [],
                [new("1", "{$competitor1}"), new("2", "draw"), new("3", "{$competitor2}")]),
            new MarketDescriptionRef(18, "Total", ["all", "score", "regular_play"], [new("total", "decimal")],
                [new("12", "over {total}"), new("13", "under {total}")]),
        ]);
        await _store.SetProducerStateAsync(1, "LO", "up", null);
        _eventId = await _store.EnsureEventAsync(new EventRef(EventUrn, "sr:sport:1", "Soccer", "sr:category:900001", "Georgia (Sim)",
            "GEO", "sr:tournament:900001", "Sim Premier League", DateTimeOffset.UtcNow,
            [new("sr:competitor:900003", "Kutaisi Eagles", "KTE", "GEO", "home"), new("sr:competitor:900004", "Rustavi Steel", "RST", "GEO", "away")]));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private async Task<IReadOnlyList<ApplyOutcome>> RunScenarioAsync(string yaml)
    {
        // Compiled messages carry timestamp 0; give each a distinct, increasing feed timestamp.
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var results = new List<ApplyOutcome>();
        foreach (var m in ScenarioCompiler.Compile(Scenario.Parse(yaml), 1))
        {
            var xml = System.Text.Encoding.UTF8.GetString(FeedMessageBuilder.WithTimestamp(m.Body, start + m.OffsetMs + results.Count));
            results.Add(await _store.ApplyAsync(UofFeedParser.Parse(xml), _eventId, xml, m.RoutingKey));
        }
        return results;
    }

    private const string Derby = """
        event: sr:match:900000002
        strength: { home: 1.5, away: 1.1 }
        steps:
          - { at: 0s,  action: prematch_odds }
          - { at: 1s,  action: kickoff }
          - { at: 2s,  action: bet_stop }
          - { at: 3s,  action: goal, team: home, minute: 20 }
          - { at: 4s,  action: goal, team: away, minute: 60 }
          - { at: 5s,  action: goal, team: home, minute: 85 }
          - { at: 6s,  action: full_time }
          - { at: 7s,  action: settle, certainty: 1 }
          - { at: 8s,  action: settle, certainty: 2 }
          - { at: 9s,  action: rollback_settlement }
          - { at: 10s, action: settle, certainty: 2 }
        """;

    [DbFact]
    public async Task Event_hierarchy_and_competitors_are_created_once()
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(_eventId, await _store.EnsureEventAsync(new EventRef(EventUrn, "sr:sport:1", "Soccer", null, null, null, null, null, null, [])));
        Assert.Equal(["Kutaisi Eagles", "Rustavi Steel"], await conn.QueryAsync<string>("""
            SELECT c.name_i18n->>'en' FROM sb.event_competitor ec JOIN sb.competitor c ON c.id = ec.competitor_id
            WHERE ec.event_id = @_eventId ORDER BY ec.position
            """, new { _eventId }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.tournament"));
    }

    [DbFact]
    public async Task Full_match_lifecycle_lands_in_the_canonical_model()
    {
        var results = await RunScenarioAsync(Derby);
        Assert.All(results, r => Assert.Equal(ApplyOutcome.Processed, r));

        await using var conn = await _db.DataSource.OpenConnectionAsync();
        var ev = await conn.QuerySingleAsync<(string Status, int Code, decimal Home, decimal Away)>(
            "SELECT status::text, match_status_code, home_score, away_score FROM sb.event WHERE id = @_eventId", new { _eventId });
        Assert.Equal(("ended", 100, 2m, 1m), ev);

        // 1x2, Total 2.5, BTTS (seeded on the fly as uof_29) - all settled after the final resettlement.
        var markets = (await conn.QueryAsync<(string Code, string Specifiers, string Status)>("""
            SELECT d.code, m.specifiers, m.status::text FROM sb.market m JOIN sb.market_description d ON d.id = m.market_description_id
            WHERE m.event_id = @_eventId ORDER BY d.code
            """, new { _eventId })).ToList();
        Assert.Equal([("uof_1", "", "settled"), ("uof_18", "total=2.5", "settled"), ("uof_29", "", "settled")], markets);

        // Exactly one effective settlement per outcome; history keeps the superseded and rolled-back ones.
        var effective = await conn.QueryAsync<(string Code, string Result, short Certainty)>("""
            SELECT s.outcome_code, s.result::text, s.certainty FROM sb.settlement s
            JOIN sb.market m ON m.id = s.market_id JOIN sb.market_description d ON d.id = m.market_description_id
            WHERE d.code = 'uof_1' AND s.rolled_back_at IS NULL AND s.superseded_by_id IS NULL ORDER BY s.outcome_code
            """);
        Assert.Equal([("1", "won", (short)2), ("2", "lost", (short)2), ("3", "lost", (short)2)], effective);
        // 7 outcomes (1x2: 3, Total: 2, BTTS: 2): certainty 1 superseded by certainty 2, which is then rolled back.
        Assert.Equal(7, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.settlement WHERE superseded_by_id IS NOT NULL"));
        Assert.Equal(7, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.settlement WHERE rolled_back_at IS NOT NULL"));
        Assert.Equal(21, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.settlement"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.rollback"));

        var outcomeName = await conn.ExecuteScalarAsync<string>("""
            SELECT mdo.name_template_i18n->>'en' FROM sb.outcome o JOIN sb.market_description_outcome mdo ON mdo.id = o.description_outcome_id
            WHERE o.code = '12'
            """);
        Assert.Equal("over {total}", outcomeName);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.bet_stop_log WHERE affected_markets > 0"));
        Assert.Equal(results.Count, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.feed_message_log WHERE status = 'processed'"));
        Assert.NotNull(await conn.ExecuteScalarAsync<DateTime?>("SELECT last_processed_feed_ts FROM sb.producer_status WHERE producer_id = 1"));
    }

    [DbFact]
    public async Task Duplicate_delivery_is_archived_but_not_applied_twice()
    {
        var xml = System.Text.Encoding.UTF8.GetString(FeedMessageBuilder.BetSettlement(1, EventUrn, 1_700_000_000_000, 1,
            [new SettlementMarket(1, [new SettlementOutcome("1", 1)])]));
        var cmd = UofFeedParser.Parse(xml);

        Assert.Equal(ApplyOutcome.Processed, await _store.ApplyAsync(cmd, _eventId, xml));
        Assert.Equal(ApplyOutcome.Duplicate, await _store.ApplyAsync(cmd, _eventId, xml));

        await using var conn = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.settlement"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.feed_message_log WHERE status = 'skipped_duplicate'"));
    }

    [DbFact]
    public async Task Stale_odds_do_not_overwrite_newer_ones()
    {
        static string Odds(long ts, double price) => System.Text.Encoding.UTF8.GetString(FeedMessageBuilder.OddsChange(1, EventUrn, ts,
            [new MarketOdds(1, [new OutcomeOdds("1", price)])]));

        var newer = Odds(2_000, 1.50);
        var older = Odds(1_000, 3.00);
        await _store.ApplyAsync(UofFeedParser.Parse(newer), _eventId, newer);
        await _store.ApplyAsync(UofFeedParser.Parse(older), _eventId, older);

        await using var conn = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(1.50m, await conn.ExecuteScalarAsync<decimal>("SELECT odds FROM sb.outcome WHERE code = '1'"));
    }

    [DbFact]
    public async Task Cancelled_match_cancels_markets()
    {
        await RunScenarioAsync("""
            event: sr:match:900000002
            steps:
              - { at: 0s, action: kickoff }
              - { at: 1s, action: cancel, void_reason: 0 }
            """);

        await using var conn = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(["cancelled"], (await conn.QueryAsync<string>("SELECT DISTINCT status::text FROM sb.market")).ToList());
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.market_cancellation"));
    }

    [DbFact]
    public async Task Failed_messages_are_logged_not_thrown()
    {
        var xml = System.Text.Encoding.UTF8.GetString(FeedMessageBuilder.BetSettlement(1, EventUrn, 1, 1,
            [new SettlementMarket(1, [new SettlementOutcome("1", 1, VoidFactor: 0.5)])]));
        // An event id that does not exist makes the FK fail inside the transaction.
        Assert.Equal(ApplyOutcome.Failed, await _store.ApplyAsync(UofFeedParser.Parse(xml), eventId: -1, xml));

        await using var conn = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM sb.feed_message_log WHERE status = 'failed' AND error IS NOT NULL"));
    }
}
