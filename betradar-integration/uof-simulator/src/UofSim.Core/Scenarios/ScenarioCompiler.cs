using System.Text;
using UofSim.Core.Messages;
using UofSim.Core.Odds;
using UofSim.Core.Recordings;
using UofSim.Core.Routing;

namespace UofSim.Core.Scenarios;

/// <summary>
/// Turns a <see cref="Scenario"/> into a timed list of feed messages (the same format as recordings),
/// tracking score, clock and status and pricing 1x2 / Total 2.5 / BTTS with <see cref="PoissonModel"/>.
/// Pure and deterministic: the same scenario always compiles to the same messages.
/// </summary>
public static class ScenarioCompiler
{
    private const int LiveOdds = 1;
    private const int Ctrl = 3;
    private const string Total25 = "total=2.5";

    private static readonly (int Id, string? Specifiers)[] SettledMarkets = [(1, null), (18, Total25), (29, null)];

    public static IReadOnlyList<RecordedMessage> Compile(Scenario scenario, int sportId)
    {
        var state = new MatchState();
        var output = new List<RecordedMessage>();

        foreach (var step in scenario.Steps.OrderBy(s => s.OffsetMs))
        {
            if (step.Minute is { } minute)
            {
                state.Minute = minute;
            }
            foreach (var (interest, type, body) in Apply(scenario, step, state))
            {
                output.Add(new RecordedMessage(
                    step.OffsetMs,
                    RoutingKey.Build(type, Priority.Hi, interest, sportId, scenario.Event),
                    Encoding.UTF8.GetString(body)));
            }
        }
        return output;
    }

    private static IEnumerable<(Interest Interest, string Type, byte[] Body)> Apply(
        Scenario sc, ScenarioStep step, MatchState state)
    {
        var e = sc.Event;
        switch (step.Action)
        {
            case ScenarioActions.PrematchOdds:
                yield return (Interest.PrematchOnly, MessageTypes.OddsChange,
                    FeedMessageBuilder.OddsChange(Ctrl, e, 0, Markets(sc, state), state.Snapshot()));
                break;

            case ScenarioActions.Kickoff:
                state.Status = EventStatus.Live;
                state.MatchStatus = 6;
                state.Minute = step.Minute ?? 0;
                yield return LiveOddsChange(sc, state);
                break;

            case ScenarioActions.OddsUpdate:
                yield return LiveOddsChange(sc, state);
                break;

            case ScenarioActions.BetStop:
                yield return (Interest.LiveOnly, MessageTypes.BetStop,
                    FeedMessageBuilder.BetStop(LiveOdds, e, 0, step.Groups ?? "all"));
                break;

            case ScenarioActions.Goal:
                if (string.Equals(step.Team, "away", StringComparison.OrdinalIgnoreCase))
                {
                    state.AwayScore++;
                }
                else if (string.Equals(step.Team, "home", StringComparison.OrdinalIgnoreCase))
                {
                    state.HomeScore++;
                }
                else
                {
                    throw new InvalidDataException($"goal at {step.At}: team must be 'home' or 'away'");
                }
                yield return LiveOddsChange(sc, state);
                break;

            case ScenarioActions.Halftime:
                state.MatchStatus = 31;
                state.Minute = 45;
                yield return LiveOddsChange(sc, state, MarketStatus.Suspended);
                break;

            case ScenarioActions.SecondHalf:
                state.MatchStatus = 7;
                state.Minute = step.Minute ?? 46;
                yield return LiveOddsChange(sc, state);
                break;

            case ScenarioActions.FullTime:
                state.Status = EventStatus.Ended;
                state.MatchStatus = 100;
                state.Minute = 90;
                yield return (Interest.LiveOnly, MessageTypes.OddsChange,
                    FeedMessageBuilder.OddsChange(LiveOdds, e, 0,
                        Markets(sc, state).Select(m => m with { Status = MarketStatus.Deactivated }),
                        state.Snapshot(withClock: false)));
                break;

            case ScenarioActions.Settle:
                yield return (Interest.LiveOnly, MessageTypes.BetSettlement,
                    FeedMessageBuilder.BetSettlement(LiveOdds, e, 0, step.Certainty ?? Certainty.LiveScouted, Settlement(state)));
                break;

            case ScenarioActions.RollbackSettlement:
                yield return (Interest.LiveOnly, MessageTypes.RollbackBetSettlement,
                    FeedMessageBuilder.RollbackBetSettlement(LiveOdds, e, 0, SettledMarkets));
                break;

            case ScenarioActions.Cancel:
                yield return (Interest.LiveOnly, MessageTypes.BetCancel,
                    FeedMessageBuilder.BetCancel(LiveOdds, e, 0, SettledMarkets, step.VoidReason));
                break;

            default:
                throw new InvalidDataException($"Unknown action '{step.Action}'");
        }
    }

    private static (Interest, string, byte[]) LiveOddsChange(Scenario sc, MatchState state, int? forceStatus = null) =>
        (Interest.LiveOnly, MessageTypes.OddsChange,
            FeedMessageBuilder.OddsChange(LiveOdds, sc.Event, 0,
                forceStatus is { } s ? Markets(sc, state).Select(m => m with { Status = s }) : Markets(sc, state),
                state.Snapshot()));

    private static List<MarketOdds> Markets(Scenario sc, MatchState state)
    {
        var p = PoissonModel.Evaluate(sc.Strength.Home, sc.Strength.Away, state.Minute, state.HomeScore, state.AwayScore);
        return
        [
            Market(1, null, false, sc.Margin, ("1", p.Home), ("2", p.Draw), ("3", p.Away)),
            Market(18, Total25, true, sc.Margin, ("12", p.Over25), ("13", 1 - p.Over25)),
            Market(29, null, false, sc.Margin, ("74", p.BothTeamsScore), ("76", 1 - p.BothTeamsScore)),
        ];
    }

    private static MarketOdds Market(int id, string? specifiers, bool favourite, double margin, params (string Id, double P)[] outcomes)
    {
        // Once an outcome is (virtually) certain the market is closed, as a trader would do.
        var decided = outcomes.Any(o => o.P >= 0.995);
        return new MarketOdds(
            id,
            outcomes.Select(o => new OutcomeOdds(o.Id, PoissonModel.ToOdds(Math.Max(o.P, 0.001), margin), Math.Round(o.P, 4))).ToList(),
            decided ? MarketStatus.Deactivated : MarketStatus.Active,
            specifiers,
            favourite);
    }

    private static List<SettlementMarket> Settlement(MatchState s)
    {
        int Result(bool won) => won ? OutcomeResult.Won : OutcomeResult.Lost;
        var goals = s.HomeScore + s.AwayScore;
        var btts = s.HomeScore > 0 && s.AwayScore > 0;
        return
        [
            new SettlementMarket(1,
            [
                new SettlementOutcome("1", Result(s.HomeScore > s.AwayScore)),
                new SettlementOutcome("2", Result(s.HomeScore == s.AwayScore)),
                new SettlementOutcome("3", Result(s.HomeScore < s.AwayScore)),
            ]),
            new SettlementMarket(18,
            [
                new SettlementOutcome("12", Result(goals > 2.5)),
                new SettlementOutcome("13", Result(goals < 2.5)),
            ], Total25),
            new SettlementMarket(29,
            [
                new SettlementOutcome("74", Result(btts)),
                new SettlementOutcome("76", Result(!btts)),
            ]),
        ];
    }

    private sealed class MatchState
    {
        public int Status { get; set; } = EventStatus.NotStarted;
        public int MatchStatus { get; set; }
        public int Minute { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }

        public SportEventStatusSnapshot Snapshot(bool withClock = true) =>
            new(Status, MatchStatus, HomeScore, AwayScore,
                withClock && Status == EventStatus.Live ? $"{Minute}:00" : null);
    }
}
