namespace Offer.Core;

/// <summary>
/// The price pipeline of docs/06 §4.2, deterministic: the same feed state, settings, gate and overrides always give
/// the same offer, so the trading view, the player API and the bet engine agree to the last digit.
/// <code>
/// 0 status   most restrictive of CFG visibility / market type / phase, trading close / suspend, feed status, producer down
/// 1 inputs   active outcomes with odds &gt; 1 (manual market: the trader's prices, steps 2-4 skipped)
/// 2 fair p   feed probabilities (margin.use_feed_probabilities) or overround removal (margin.remove_method)
/// 3 margin   mode feed: unchanged · target: Σ1/o = 1 + margin.pct (margin.method) · delta: o' = 1 + (o−1)(1−d)
///            target on an incomplete market falls back to delta
/// 4 override absolute odds or shift_pct of the priced odds; expired / feed-moved overrides are ignored
/// 6 clamp    below odds.min: outcome hidden · above odds.max: capped
/// 7 ladder   rounded down to odds.ladder
/// 8 sanity   complete market with Σ1/o &lt; 1 + margin.floor_pct: suspended
/// </code>
/// Step 5 (PROMO price boost) joins with PROMO.
/// </summary>
public static class OfferPricer
{
    public static PricedMarket Price(
        MarketInput market, PricingSettings settings, MarketGate gate, IReadOnlyList<OddsOverride> overrides, DateTime now)
    {
        var reasons = new List<(OfferStatus Status, string Reason)>();

        // 0. Status sources.
        if (!gate.Visible)
        {
            reasons.Add((OfferStatus.Hidden, "cfg:offer.visible"));
        }
        if (!gate.MarketTypeEnabled)
        {
            reasons.Add((OfferStatus.Hidden, "cfg:market.enabled"));
        }
        if (!gate.PhaseEnabled)
        {
            reasons.Add((OfferStatus.Hidden, gate.EventLive ? "cfg:offer.live_enabled" : "cfg:offer.prematch_enabled"));
        }
        if (gate.TradingClosed)
        {
            reasons.Add((OfferStatus.Hidden, "trading:close"));
        }
        var feedStatus = ParseStatus(market.FeedStatus);
        if (feedStatus != OfferStatus.Active)
        {
            reasons.Add((feedStatus, $"feed:{market.FeedStatus}"));
        }
        else if (ParseStatus(market.Status) == OfferStatus.Suspended)
        {
            // The feed says active but the adapter holds it: its producer is down (V003).
            reasons.Add((gate.ProducerDownPolicy == "hide" ? OfferStatus.Hidden : OfferStatus.Suspended, "producer_down"));
        }
        if (gate.TradingSuspended)
        {
            reasons.Add((OfferStatus.Suspended, "trading:suspend"));
        }
        if (market.IsManual && gate.EventLive && !gate.ManualMarketLive)
        {
            reasons.Add((OfferStatus.Suspended, "manual:prematch_only"));
        }

        // 1. Inputs and completeness.
        var priceable = market.Outcomes.Where(o => o.IsActive && o.Odds > 1).ToList();
        var q = priceable.Select(o => 1 / (double)o.Odds!.Value).ToArray();
        var structurallyComplete = market.ClosedSet && priceable.Count >= 2 && priceable.Count == market.Outcomes.Count
                                   && (market.DefinedOutcomes is null || market.DefinedOutcomes == priceable.Count);
        // A feed booksum below 1 means we only have part of the outcomes; a manual one is checked by the sanity step.
        var complete = structurallyComplete && (market.IsManual || q.Sum() >= 1 - 1e-9);

        var mode = market.IsManual ? "manual"
            : settings.Mode switch
            {
                "target" => complete ? "target" : "delta",
                "delta" => "delta",
                _ => "feed",
            };

        // 2. Fair probabilities (also shown for feed / delta modes when the market is complete).
        double[]? fair = null;
        if (complete && !market.IsManual)
        {
            fair = FeedProbabilities(priceable, settings) ?? Margin.Remove(q, settings.RemoveMethod);
        }

        // 3. Margin.
        var priced = new double[priceable.Count];
        double[]? target = mode == "target" ? Margin.Apply(fair!, (double)settings.Pct, settings.Method) : null;
        for (var i = 0; i < priceable.Count; i++)
        {
            var o = (double)priceable[i].Odds!.Value;
            priced[i] = mode switch
            {
                "target" => 1 / target![i],
                "delta" => Margin.Delta(o, (double)settings.DeltaPct),
                _ => o,
            };
        }

        var outcomes = new List<PricedOutcome>(market.Outcomes.Count);
        var visibleOdds = new List<decimal>();
        foreach (var outcome in market.Outcomes)
        {
            var index = priceable.IndexOf(outcome);
            if (index < 0)
            {
                outcomes.Add(new PricedOutcome(outcome.Code, outcome.Odds, null, null, "feed", false, "inactive", null));
                continue;
            }
            var odds = priced[index];
            var source = mode switch
            {
                "manual" => "manual",
                "feed" => "feed",
                _ => "margin",
            };

            // 4. Manual override (not on manual markets: the trader edits their prices directly).
            long? overrideId = null;
            if (!market.IsManual && overrides.FirstOrDefault(x => x.OutcomeCode == outcome.Code && IsLive(x, outcome.Odds, settings, now)) is { } ov)
            {
                odds = ov.Kind == "shift_pct" ? odds * (1 + (double)ov.Value) : (double)ov.Value;
                source = "override";
                overrideId = ov.Id;
            }

            // 6. Clamp, 7. ladder.
            decimal? final = null;
            string? hidden = null;
            var value = (decimal)Math.Round(odds, 6);
            if (value < settings.MinOdds)
            {
                hidden = "below_min";
            }
            else
            {
                final = OddsLadder.Floor(Math.Min(value, settings.MaxOdds), settings.Ladder);
                hidden = final is null ? "below_min" : null;
            }
            if (final is { } f)
            {
                visibleOdds.Add(f);
            }
            outcomes.Add(new PricedOutcome(outcome.Code, outcome.Odds, fair is null ? null : Math.Round((decimal)fair[index], 6),
                final, source, final is not null, hidden, overrideId));
        }

        // 8. Sanity: a complete market must keep at least the floor margin.
        decimal? offerOverround = null;
        if (complete && visibleOdds.Count == priceable.Count)
        {
            var booksum = visibleOdds.Sum(o => 1 / o);
            offerOverround = Math.Round(booksum - 1, 4);
            if (booksum < 1 + settings.FloorPct)
            {
                reasons.Add((OfferStatus.Suspended, "sanity:margin_floor"));
            }
        }

        var ordered = reasons.OrderByDescending(r => r.Status).ToList();
        return new PricedMarket(
            market.MarketId,
            ordered.Count > 0 ? ordered[0].Status : OfferStatus.Active,
            ordered.Select(r => r.Reason).ToList(),
            mode,
            complete,
            complete && !market.IsManual ? Math.Round((decimal)q.Sum() - 1, 4) : null,
            offerOverround,
            outcomes);
    }

    /// <summary>An override applies until it expires; <c>clear_on=feed_change</c> also ends it once the feed moved past the tolerance.</summary>
    public static bool IsLive(OddsOverride o, decimal? feedOdds, PricingSettings settings, DateTime now)
    {
        if (o.ExpiresAt <= now)
        {
            return false;
        }
        if (o.ClearOn == "feed_change" && o.FeedOddsAtSet is { } at && at > 0 && feedOdds is { } current)
        {
            return Math.Abs(current - at) / at <= settings.OverrideFeedTolerancePct;
        }
        return true;
    }

    public static OfferStatus ParseStatus(string status) => status switch
    {
        "active" => OfferStatus.Active,
        "suspended" => OfferStatus.Suspended,
        "deactivated" => OfferStatus.Deactivated,
        "settled" => OfferStatus.Settled,
        "cancelled" => OfferStatus.Cancelled,
        _ => OfferStatus.Hidden,
    };

    /// <summary>UOF probabilities, when enabled and present for every outcome and summing to about 1.</summary>
    private static double[]? FeedProbabilities(List<OutcomeInput> outcomes, PricingSettings settings)
    {
        if (!settings.UseFeedProbabilities || outcomes.Any(o => o.Probability is not > 0))
        {
            return null;
        }
        var p = outcomes.Select(o => (double)o.Probability!.Value).ToArray();
        var sum = p.Sum();
        return Math.Abs(sum - 1) <= 0.05 ? p.Select(x => x / sum).ToArray() : null;
    }
}
