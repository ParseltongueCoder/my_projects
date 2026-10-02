using System.Text;
using UofSim.Core.Messages;
using UofSim.Core.Routing;

namespace UofSim.Core.Recordings;

/// <summary>
/// A synthetic football match (all ids, names and odds are made up) that walks through the
/// basic UOF lifecycle: pre-match odds → live → bet_stop on a goal → re-open → full time → settlement.
/// Used to produce <c>data/recordings/demo_match.jsonl</c> for the L1 replayer.
/// </summary>
public static class DemoMatch
{
    public const string EventUrn = "sr:match:900000001";
    public const int SportId = 1;

    private const int Ctrl = 3;
    private const int LiveOdds = 1;
    private const string Total = "total=2.5";

    public static IReadOnlyList<RecordedMessage> Build()
    {
        var messages = new List<RecordedMessage>();
        void Add(long offsetMs, string type, Interest interest, byte[] body) =>
            messages.Add(new RecordedMessage(
                offsetMs,
                RoutingKey.Build(type, Priority.Hi, interest, SportId, EventUrn),
                Encoding.UTF8.GetString(body)));

        // Timestamps inside bodies are placeholders: the replayer rewrites them to "now".
        Add(0, MessageTypes.OddsChange, Interest.PrematchOnly, FeedMessageBuilder.OddsChange(
            Ctrl, EventUrn, 0,
            [Match1X2(2.10, 3.30, 3.60), Total25(1.95, 1.85)],
            new SportEventStatusSnapshot(EventStatus.NotStarted, 0)));

        Add(5_000, MessageTypes.OddsChange, Interest.LiveOnly, FeedMessageBuilder.OddsChange(
            LiveOdds, EventUrn, 0,
            [Match1X2(2.05, 3.35, 3.70), Total25(1.92, 1.88)],
            new SportEventStatusSnapshot(EventStatus.Live, 6, 0, 0, "1:00")));

        Add(15_000, MessageTypes.BetStop, Interest.LiveOnly, FeedMessageBuilder.BetStop(
            LiveOdds, EventUrn, 0, groups: "all"));

        Add(18_000, MessageTypes.OddsChange, Interest.LiveOnly, FeedMessageBuilder.OddsChange(
            LiveOdds, EventUrn, 0,
            [Match1X2(1.45, 4.20, 7.00), Total25(1.70, 2.10)],
            new SportEventStatusSnapshot(EventStatus.Live, 6, 1, 0, "23:10")));

        Add(30_000, MessageTypes.OddsChange, Interest.LiveOnly, FeedMessageBuilder.OddsChange(
            LiveOdds, EventUrn, 0,
            [Match1X2(1.40, 4.40, 7.50, MarketStatus.Suspended), Total25(1.75, 2.00, MarketStatus.Suspended)],
            new SportEventStatusSnapshot(EventStatus.Live, 31, 1, 0, "45:00")));

        Add(40_000, MessageTypes.OddsChange, Interest.LiveOnly, FeedMessageBuilder.OddsChange(
            LiveOdds, EventUrn, 0,
            [Match1X2(1.20, 5.50, 13.00), Total25(2.40, 1.55)],
            new SportEventStatusSnapshot(EventStatus.Live, 7, 1, 0, "70:00")));

        Add(50_000, MessageTypes.OddsChange, Interest.LiveOnly, FeedMessageBuilder.OddsChange(
            LiveOdds, EventUrn, 0,
            [Match1X2(null, null, null, MarketStatus.Deactivated), Total25(null, null, MarketStatus.Deactivated)],
            new SportEventStatusSnapshot(EventStatus.Ended, 100, 1, 0)));

        Add(52_000, MessageTypes.BetSettlement, Interest.LiveOnly, FeedMessageBuilder.BetSettlement(
            LiveOdds, EventUrn, 0, Certainty.LiveScouted,
            [
                new SettlementMarket(1,
                [
                    new SettlementOutcome("1", OutcomeResult.Won),
                    new SettlementOutcome("2", OutcomeResult.Lost),
                    new SettlementOutcome("3", OutcomeResult.Lost),
                ]),
                new SettlementMarket(18,
                [
                    new SettlementOutcome("12", OutcomeResult.Lost),
                    new SettlementOutcome("13", OutcomeResult.Won),
                ], Total),
            ]));

        return messages;
    }

    private static MarketOdds Match1X2(double? home, double? draw, double? away, int status = MarketStatus.Active) =>
        new(1,
        [
            new OutcomeOdds("1", home, Implied(home)),
            new OutcomeOdds("2", draw, Implied(draw)),
            new OutcomeOdds("3", away, Implied(away)),
        ], status);

    private static MarketOdds Total25(double? over, double? under, int status = MarketStatus.Active) =>
        new(18,
        [
            new OutcomeOdds("12", over, Implied(over)),
            new OutcomeOdds("13", under, Implied(under)),
        ], status, Total, Favourite: true);

    private static double? Implied(double? odds) => odds is { } o ? Math.Round(1 / o, 4) : null;
}
