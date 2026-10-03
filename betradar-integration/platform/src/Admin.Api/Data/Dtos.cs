namespace Admin.Api.Data;

// Timestamps are UTC DateTime (what Npgsql returns for timestamptz); JSON renders them with a trailing Z.

public sealed record ProducerDto(int Id, string Name, string State, string? DownReason, DateTime? LastProcessedFeedTs, DateTime UpdatedAt);

public sealed record OverviewDto(
    IReadOnlyList<ProducerDto> Producers,
    long LiveEvents,
    long UpcomingEvents,
    long ActiveMarkets,
    long SuspendedMarkets,
    long MessagesLastHour,
    long FailedLastHour,
    DateTime? LastMessageAt);

public sealed record EventSummaryDto(
    long Id,
    string? Urn,
    string Sport,
    string? Category,
    string? Tournament,
    string? Home,
    string? Away,
    DateTime? ScheduledAt,
    string Status,
    int? MatchStatusCode,
    decimal? HomeScore,
    decimal? AwayScore,
    string? Clock,
    DateTime? LastFeedAt,
    long Markets,
    long ActiveMarkets)
{
    public string Name => Home is not null && Away is not null ? $"{Home} v {Away}" : Urn ?? $"#{Id}";
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasMore, long? Total = null);

public sealed record OutcomeDto(
    string Code, string Name, decimal? Odds, decimal? Probability, bool Active,
    string? Result, decimal? VoidFactor, decimal? DeadHeatFactor, short? Certainty);

/// <param name="Status">What we offer (effective status).</param>
/// <param name="FeedStatus">What the feed last said; differs while the producer is down.</param>
public sealed record MarketDto(
    long Id, string? MarketTypeId, string Name, string Template, string Specifiers,
    string Status, string FeedStatus, short? ProducerId, bool Favourite, DateTime LastFeedAt,
    IReadOnlyList<OutcomeDto> Outcomes);

/// <param name="State"><c>effective</c>, <c>superseded</c> (replaced by a later settlement) or <c>rolled_back</c>.</param>
public sealed record SettlementDto(
    long Id, long MarketId, string Market, string OutcomeCode, string Outcome, string Result, short Certainty,
    decimal? VoidFactor, decimal? DeadHeatFactor, short ProducerId, DateTime FeedTs,
    DateTime? RolledBackAt, long? SupersededById, string State);

public sealed record BetStopDto(long Id, string Source, string? Groups, string TargetStatus, int? AffectedMarkets, short ProducerId, DateTime FeedTs);

public sealed record FeedMessageDto(
    long Id, DateTime ReceivedAt, string Type, string? EventUrn, long? EventId, short? ProducerId,
    long? RequestId, DateTime? FeedTs, string Status, string? Error, double? LagMs);

public sealed record FeedMessageDetailDto(FeedMessageDto Message, string Payload);

public sealed record EventDetailDto(EventSummaryDto Event, IReadOnlyList<MarketDto> Markets);
