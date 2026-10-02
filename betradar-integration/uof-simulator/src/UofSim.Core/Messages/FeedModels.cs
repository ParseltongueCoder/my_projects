namespace UofSim.Core.Messages;

/// <summary>UOF market status values (AMQP <c>market@status</c>).</summary>
public static class MarketStatus
{
    public const int Active = 1;
    public const int Deactivated = 0;
    public const int Suspended = -1;
    public const int HandedOver = -2;
    public const int Settled = -3;
    public const int Cancelled = -4;
}

/// <summary>UOF sport event status values (AMQP <c>sport_event_status@status</c>).</summary>
public static class EventStatus
{
    public const int NotStarted = 0;
    public const int Live = 1;
    public const int Suspended = 2;
    public const int Ended = 3;
    public const int Closed = 4;
}

public static class OutcomeResult
{
    public const int Lost = 0;
    public const int Won = 1;
}

public static class Certainty
{
    public const int LiveScouted = 1;
    public const int Confirmed = 2;
}

public sealed record OutcomeOdds(string Id, double? Odds, double? Probability = null, bool Active = true);

/// <param name="Specifiers">Raw specifier string as sent on the feed, e.g. <c>total=2.5</c>.</param>
public sealed record MarketOdds(
    int Id,
    IReadOnlyList<OutcomeOdds> Outcomes,
    int Status = MarketStatus.Active,
    string? Specifiers = null,
    bool Favourite = false);

public sealed record SportEventStatusSnapshot(
    int Status,
    int MatchStatus,
    int HomeScore = 0,
    int AwayScore = 0,
    string? MatchTime = null);

public sealed record SettlementOutcome(string Id, int Result, double? VoidFactor = null, double? DeadHeatFactor = null);

public sealed record SettlementMarket(int Id, IReadOnlyList<SettlementOutcome> Outcomes, string? Specifiers = null);
