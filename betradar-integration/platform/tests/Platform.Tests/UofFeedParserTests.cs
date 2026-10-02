using System.Text;
using Platform.Canonical.Feed;
using UofSim.Core.Messages;

namespace Platform.Tests;

/// <summary>Parses messages produced by the simulator's builders (same XML the SDK hands us as RawMessage).</summary>
public class UofFeedParserTests
{
    private static FeedCommand Parse(byte[] xml) => UofFeedParser.Parse(Encoding.UTF8.GetString(xml));

    [Fact]
    public void Odds_change_is_parsed_with_status_and_sorted_specifiers()
    {
        var cmd = Assert.IsType<OddsChange>(Parse(FeedMessageBuilder.OddsChange(1, "sr:match:5", 1_700_000_000_000,
            [new MarketOdds(16, [new OutcomeOdds("1714", 1.9, 0.5), new OutcomeOdds("1715", 1.9, Active: false)],
                MarketStatus.Active, "variant=x|hcp=-1")],
            new SportEventStatusSnapshot(EventStatus.Live, 6, 1, 0, "12:00"))));

        Assert.Equal("sr:match:5", cmd.EventUrn);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), cmd.FeedTs);
        Assert.Equal(new EventStatusUpdate("live", 6, 1, 0, "12:00"), cmd.EventStatus);
        var market = Assert.Single(cmd.Markets);
        Assert.Equal("hcp=-1|variant=x", market.Specifiers);
        Assert.Equal("active", market.Status);
        Assert.Equal(1.9m, market.Outcomes[0].Odds);
        Assert.False(market.Outcomes[1].Active);
        Assert.Null(market.Outcomes[1].Odds);
    }

    [Theory]
    [InlineData(MarketStatus.Active, "active")]
    [InlineData(MarketStatus.Suspended, "suspended")]
    [InlineData(MarketStatus.Deactivated, "deactivated")]
    [InlineData(MarketStatus.HandedOver, null)]
    public void Market_status_is_mapped(int uof, string? canonical)
    {
        var cmd = Assert.IsType<OddsChange>(Parse(FeedMessageBuilder.OddsChange(1, "sr:match:5", 1,
            [new MarketOdds(1, [new OutcomeOdds("1", 2.0)], uof)])));
        Assert.Equal(canonical, cmd.Markets.Single().Status);
    }

    [Fact]
    public void Bet_stop_defaults_to_suspended()
    {
        var cmd = Assert.IsType<BetStop>(Parse(FeedMessageBuilder.BetStop(1, "sr:match:5", 1, "score|1st_half")));
        Assert.Equal(["score", "1st_half"], cmd.Groups);
        Assert.Equal("suspended", cmd.TargetStatus);
    }

    [Fact]
    public void Settlement_rollback_and_cancel()
    {
        var settlement = Assert.IsType<BetSettlement>(Parse(FeedMessageBuilder.BetSettlement(1, "sr:match:5", 1, Certainty.Confirmed,
            [new SettlementMarket(18, [new SettlementOutcome("12", OutcomeResult.Won), new SettlementOutcome("13", OutcomeResult.Lost, VoidFactor: 0.5)], "total=2.25")])));
        Assert.Equal(2, settlement.Certainty);
        Assert.Equal(new OutcomeSettlement("13", "lost", 0.5m, null), settlement.Markets.Single().Outcomes[1]);

        var rollback = Assert.IsType<RollbackBetSettlement>(Parse(FeedMessageBuilder.RollbackBetSettlement(1, "sr:match:5", 1, [(18, "total=2.25")])));
        Assert.Equal(new MarketRef(18, "total=2.25", null), rollback.Markets.Single());

        var cancel = Assert.IsType<BetCancel>(Parse(FeedMessageBuilder.BetCancel(1, "sr:match:5", 1, [(1, null)], voidReason: 3)));
        Assert.Equal(3, cancel.Markets.Single().VoidReason);
    }

    [Fact]
    public void Unknown_message_types_are_kept_for_the_archive()
    {
        var cmd = UofFeedParser.Parse("""<fixture_change product="3" event_id="sr:match:5" timestamp="1" change_type="2"/>""");
        Assert.Equal("fixture_change", Assert.IsType<UnhandledMessage>(cmd).MessageType);
    }
}
