using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using UofSim.Core.Messages;
using UofSim.Core.Recovery;

namespace UofSim.Tests;

public class RecoverySnapshotTests
{
    private const string Event = "sr:match:900000001";
    private const string Key = "hi.-.live.odds_change.1.sr:match.900000001.-";

    private static byte[] Odds(int product, int status = MarketStatus.Active) =>
        FeedMessageBuilder.OddsChange(product, Event, 1, [new MarketOdds(1, [new OutcomeOdds("1", 2.0)], status)]);

    [Fact]
    public void Keeps_the_latest_odds_per_producer_and_event()
    {
        var store = new SnapshotStore();
        store.Observe(Key, Odds(1));
        store.Observe(Key, Odds(1, MarketStatus.Suspended));
        store.Observe("hi.pre.-.odds_change.1.sr:match.900000001.-", Odds(3));

        var live = Assert.Single(store.ForProducer(1));
        Assert.Contains("status=\"-1\"", live.Body);
        Assert.Equal(Key, live.RoutingKey);
        Assert.Single(store.ForProducer(3, Event));
        Assert.Empty(store.ForProducer(1, "sr:match:42"));
    }

    [Fact]
    public void Bet_stop_after_the_last_odds_suspends_the_snapshot()
    {
        var store = new SnapshotStore();
        store.Observe(Key, Odds(1));
        store.Observe("hi.-.live.bet_stop.1.sr:match.900000001.-", FeedMessageBuilder.BetStop(1, Event, 2));

        var market = XElement.Parse(store.ForProducer(1).Single().Body).Descendants("market").Single();
        Assert.Equal("-1", (string?)market.Attribute("status"));
    }

    [Fact]
    public async Task Recovery_resends_current_odds_with_request_id_before_snapshot_complete()
    {
        await using var sim = new SimFactory();
        var client = sim.CreateClient();
        await client.PostAsJsonAsync("/sim/replay", new { file = "recordings/demo_match.jsonl", speed = 1000.0 });
        await sim.Publisher.WaitForAsync(m => m.Body.Contains("<bet_settlement"));

        var response = await sim.CreateApiClient().PostAsync("/v1/liveodds/recovery/initiate_request?request_id=777&node_id=2", null);
        response.EnsureSuccessStatusCode();
        await sim.Publisher.WaitForAsync(m => m.RoutingKey == "-.-.-.snapshot_complete.-.-.-.2");

        var answered = sim.Publisher.Messages.Where(m => m.Body.Contains("request_id=\"777\"")).ToList();
        Assert.Equal(["odds_change", "snapshot_complete"], answered.Select(m => XDocument.Parse(m.Body).Root!.Name.LocalName));
        Assert.Equal("1", (string?)XDocument.Parse(answered[0].Body).Root!.Attribute("product"));
    }

    [XsdFact]
    public void Restamped_snapshot_stays_schema_valid()
    {
        var validator = XsdValidator.TryLoad(XsdFactAttribute.XsdDir, XsdValidator.FeedSchema)!;
        var body = FeedMessageBuilder.Restamp(Encoding.UTF8.GetString(Odds(1)), 5, requestId: 9);
        Assert.Empty(validator.Validate(body));
    }
}
