using System.Text;
using System.Xml.Linq;
using UofSim.Core.Messages;

namespace UofSim.Tests;

public class FeedMessageBuilderTests
{
    private static XElement Parse(byte[] xml) => XDocument.Parse(Encoding.UTF8.GetString(xml)).Root!;

    [Fact]
    public void Odds_change_writes_status_markets_and_outcomes()
    {
        var root = Parse(FeedMessageBuilder.OddsChange(1, "sr:match:1", 1700000000000,
            [new MarketOdds(18, [new OutcomeOdds("12", 1.95, 0.5128), new OutcomeOdds("13", 1.85)], Specifiers: "total=2.5")],
            new SportEventStatusSnapshot(EventStatus.Live, 6, 1, 0, "23:10"),
            requestId: 77));

        Assert.Equal("odds_change", root.Name.LocalName);
        Assert.Equal("1", (string?)root.Attribute("product"));
        Assert.Equal("77", (string?)root.Attribute("request_id"));
        Assert.Equal("23:10", (string?)root.Element("sport_event_status")!.Element("clock")!.Attribute("match_time"));

        var market = root.Element("odds")!.Element("market")!;
        Assert.Equal("total=2.5", (string?)market.Attribute("specifiers"));
        var over = market.Elements("outcome").First();
        Assert.Equal("1.95", (string?)over.Attribute("odds"));
        Assert.Equal("0.5128", (string?)over.Attribute("probabilities"));
        Assert.Null(market.Elements("outcome").Last().Attribute("probabilities"));
    }

    [Theory]
    [InlineData(MarketStatus.Deactivated)]
    [InlineData(MarketStatus.Settled)]
    [InlineData(MarketStatus.Cancelled)]
    public void Closed_markets_are_sent_without_outcomes(int status)
    {
        var root = Parse(FeedMessageBuilder.OddsChange(1, "sr:match:1", 1,
            [new MarketOdds(1, [new OutcomeOdds("1", 2.0)], status)]));
        Assert.Empty(root.Element("odds")!.Element("market")!.Elements("outcome"));
    }

    [Fact]
    public void Inactive_outcome_has_no_odds()
    {
        var root = Parse(FeedMessageBuilder.OddsChange(1, "sr:match:1", 1,
            [new MarketOdds(1, [new OutcomeOdds("1", 2.0, Active: false)], MarketStatus.Suspended)]));
        var outcome = root.Descendants("outcome").Single();
        Assert.Null(outcome.Attribute("odds"));
        Assert.Equal("0", (string?)outcome.Attribute("active"));
    }

    [Fact]
    public void Bet_settlement_carries_certainty_and_void_factor()
    {
        var root = Parse(FeedMessageBuilder.BetSettlement(1, "sr:match:1", 1, Certainty.Confirmed,
            [new SettlementMarket(16, [new SettlementOutcome("1714", OutcomeResult.Won, VoidFactor: 0.5)], "hcp=-0.25")]));
        Assert.Equal("2", (string?)root.Attribute("certainty"));
        var outcome = root.Descendants("outcome").Single();
        Assert.Equal("0.5", (string?)outcome.Attribute("void_factor"));
        Assert.Equal("hcp=-0.25", (string?)root.Descendants("market").Single().Attribute("specifiers"));
    }

    [Fact]
    public void Alive_and_snapshot_complete()
    {
        var alive = Parse(FeedMessageBuilder.Alive(3, 42, subscribed: false));
        Assert.Equal("0", (string?)alive.Attribute("subscribed"));

        var snapshot = Parse(FeedMessageBuilder.SnapshotComplete(1, 42, 9001));
        Assert.Equal("9001", (string?)snapshot.Attribute("request_id"));
    }

    [Fact]
    public void WithTimestamp_rewrites_only_the_root_timestamp()
    {
        var original = Encoding.UTF8.GetString(FeedMessageBuilder.BetStop(1, "sr:match:1", 0, "all"));
        var root = Parse(FeedMessageBuilder.WithTimestamp(original, 1234));
        Assert.Equal("1234", (string?)root.Attribute("timestamp"));
        Assert.Equal("all", (string?)root.Attribute("groups"));
    }
}
