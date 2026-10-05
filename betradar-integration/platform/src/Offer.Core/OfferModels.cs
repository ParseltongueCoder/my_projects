namespace Offer.Core;

/// <summary>What a player sees for a market. Ordered by precedence: the most restrictive source wins (docs/06 §4.4).</summary>
public enum OfferStatus
{
    Active = 0,
    Suspended = 1,
    Deactivated = 2,
    Settled = 3,
    Cancelled = 4,
    Hidden = 5,
}

/// <summary>One outcome as the feed (or, for a manual market, the trader) prices it.</summary>
public sealed record OutcomeInput(string Code, decimal? Odds, decimal? Probability = null, bool IsActive = true);

/// <summary>
/// A market from the canonical model. <paramref name="FeedStatus"/> is what the provider said, <paramref name="Status"/>
/// the adapter's effective status (suspended while the producer is down, V003). <paramref name="ClosedSet"/>: the outcome
/// kind is static/variant (the outcomes cover every result); <paramref name="DefinedOutcomes"/>: how many outcomes the
/// market type defines for this variant, when known.
/// </summary>
public sealed record MarketInput(
    long MarketId,
    string FeedStatus,
    string Status,
    IReadOnlyList<OutcomeInput> Outcomes,
    bool IsManual = false,
    bool ClosedSet = true,
    int? DefinedOutcomes = null);

/// <summary>Effective CFG values for one market (docs/06 §5.8: <c>margin.*</c>, <c>odds.*</c>).</summary>
public sealed record PricingSettings(
    string Mode = "feed",
    decimal Pct = 0.06m,
    decimal DeltaPct = 0.02m,
    string Method = "power",
    string RemoveMethod = "power",
    bool UseFeedProbabilities = false,
    decimal FloorPct = 0m,
    decimal MinOdds = 1.01m,
    decimal MaxOdds = 1001m,
    string Ladder = "std",
    decimal OverrideFeedTolerancePct = 0.10m);

/// <summary>
/// Operator policy and trading state that decide visibility and status (docs/06 §4.4-4.5). Every flag is already
/// resolved by the caller (CFG all-path values, active <c>trading_override</c> rows on the event or market).
/// </summary>
public sealed record MarketGate(
    bool Visible = true,
    bool MarketTypeEnabled = true,
    bool PhaseEnabled = true,
    bool TradingClosed = false,
    bool TradingSuspended = false,
    string ProducerDownPolicy = "suspend",
    bool EventLive = false,
    bool ManualMarketLive = false);

/// <summary>A manual price on one outcome (docs/06 §4.3): <c>absolute</c> odds or <c>shift_pct</c> of the priced odds.</summary>
public sealed record OddsOverride(
    long Id,
    string OutcomeCode,
    string Kind,
    decimal Value,
    DateTime ExpiresAt,
    string ClearOn = "expiry",
    decimal? FeedOddsAtSet = null);

public sealed record PricedOutcome(
    string Code,
    decimal? FeedOdds,
    decimal? FairProbability,
    decimal? Odds,
    string Source,
    bool Visible,
    string? HiddenReason,
    long? OverrideId);

/// <summary>
/// The offer for one market. <c>Reasons</c> lists every source that restricts it (<c>cfg:offer.visible</c>,
/// <c>trading:suspend</c>, <c>feed:suspended</c>, <c>producer_down</c>, <c>sanity:margin_floor</c> ...), the winner first.
/// Overrounds are <c>Σ1/o − 1</c> and only reported for complete markets.
/// </summary>
public sealed record PricedMarket(
    long MarketId,
    OfferStatus Status,
    IReadOnlyList<string> Reasons,
    string ModeUsed,
    bool Complete,
    decimal? FeedOverround,
    decimal? OfferOverround,
    IReadOnlyList<PricedOutcome> Outcomes);
