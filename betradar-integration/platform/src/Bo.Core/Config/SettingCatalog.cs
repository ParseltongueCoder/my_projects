using System.Text.Json.Nodes;

namespace Bo.Core.Config;

/// <summary>
/// The settings catalog (docs/06 §5.8 plus docs/10 §3.1 referral keys). Code is the source of truth and is synced
/// into <c>bo.setting_def</c> at startup. Scope letters: P platform, O operator (implies brand), s sport,
/// c category, t tournament, e event, m market; <c>mt</c> = market type qualifier allowed.
/// </summary>
public static class SettingCatalog
{
    private static readonly JsonNode? None = null;

    private static JsonNode Gel(decimal amount) => new JsonObject { ["GEL"] = amount };

    private static IReadOnlyList<ScopeType> S(string letters)
    {
        var scopes = new List<ScopeType>();
        foreach (var part in letters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "P": scopes.Add(ScopeType.Platform); break;
                case "O": scopes.Add(ScopeType.Operator); scopes.Add(ScopeType.Brand); break;
                case "s": scopes.Add(ScopeType.Sport); break;
                case "c": scopes.Add(ScopeType.Category); break;
                case "t": scopes.Add(ScopeType.Tournament); break;
                case "e": scopes.Add(ScopeType.Event); break;
                case "m": scopes.Add(ScopeType.Market); break;
                default: throw new ArgumentException($"Unknown scope letter '{part}'");
            }
        }
        return scopes;
    }

    private static SettingDef Bool(string key, string module, string scopes, bool def, string description,
        Combine combine = Combine.Override, bool mt = false, bool approval = false, bool operatorEditable = true) =>
        new(key, module, ValueType.Bool, S(scopes), JsonValue.Create(def), description, combine, mt,
            OperatorEditable: operatorEditable, RequiresApproval: approval);

    private static SettingDef Int(string key, string module, string scopes, int? def, string description,
        decimal? min = null, decimal? max = null, bool mt = false, bool operatorEditable = true, string customer = "none") =>
        new(key, module, ValueType.Int, S(scopes), def is { } d ? JsonValue.Create(d) : None, description, Combine.Override, mt,
            Min: min, Max: max, OperatorEditable: operatorEditable, CustomerCombine: customer);

    private static SettingDef Dec(string key, string module, string scopes, decimal? def, string description,
        decimal? min = null, decimal? max = null, bool mt = false, bool approval = false, string customer = "none") =>
        new(key, module, ValueType.Decimal, S(scopes), def is { } d ? JsonValue.Create(d) : None, description, Combine.Override, mt,
            Min: min, Max: max, RequiresApproval: approval, CustomerCombine: customer);

    private static SettingDef Money(string key, string module, string scopes, decimal? gel, string description,
        bool mt = false, bool approval = false, string customer = "none") =>
        new(key, module, ValueType.Money, S(scopes), gel is { } g ? Gel(g) : None, description, Combine.Override, mt,
            Min: 0, RequiresApproval: approval, CustomerCombine: customer);

    private static SettingDef Enum(string key, string module, string scopes, string def, string[] values, string description,
        bool mt = false, bool approval = false) =>
        new(key, module, ValueType.Enum, S(scopes), JsonValue.Create(def), description, Combine.Override, mt, values,
            RequiresApproval: approval);

    private static SettingDef List(string key, string module, string scopes, string[] def, string description) =>
        new(key, module, ValueType.StringList, S(scopes), new JsonArray([.. def.Select(v => (JsonNode)JsonValue.Create(v))]), description);

    public static readonly IReadOnlyList<SettingDef> All =
    [
        // CAT / display
        Bool("offer.visible", "CAT", "P O s c t e m", true, "Show in the offer (hidden at a parent hides all children)", Combine.AllPath),
        Bool("offer.prematch_enabled", "CAT", "P O s c t e", true, "Prematch betting available", Combine.AllPath),
        Bool("offer.live_enabled", "CAT", "P O s c t e", true, "Live betting available", Combine.AllPath),
        Bool("catalog.new_nodes_visible", "CAT", "O s", true, "New sports / leagues from the feed are visible automatically"),
        new("display.home_country", "CAT", ValueType.String, S("O"), JsonValue.Create("GEO"), "Home country (ISO-3) sorted first"),
        Int("display.main_market_type", "CAT", "O s t", null, "Main market type shown in event lists"),
        Int("display.top_leagues_limit", "CAT", "O s", 10, "Number of top leagues", 0, 50),
        Int("display.featured_limit", "CAT", "O s", 8, "Number of featured events", 0, 50),

        // ODDS
        Bool("market.enabled", "ODDS", "P O s c t e m", true, "Market type offered", Combine.AllPath, mt: true),
        Int("market.max_lines", "ODDS", "O s t e", 5, "Max lines per market type (e.g. totals)", 1, 50, mt: true),
        Enum("margin.mode", "ODDS", "O s c t e m", "feed", ["feed", "target", "delta"], "How the operator margin is applied", mt: true, approval: true),
        Dec("margin.pct", "ODDS", "O s c t e m", 0.06m, "Target overround (target mode)", 0, 0.30m, mt: true, approval: true),
        Dec("margin.delta_pct", "ODDS", "O s c t e m", 0.02m, "Margin added on top of the feed (delta mode)", 0, 0.20m, mt: true, approval: true),
        Enum("margin.method", "ODDS", "O s", "power", ["power", "proportional", "shin"], "Margin application method", mt: true),
        Enum("margin.remove_method", "ODDS", "O s", "power", ["power", "proportional", "shin"], "Feed overround removal method", mt: true),
        Bool("margin.use_feed_probabilities", "ODDS", "O s", false, "Use UOF probabilities when present"),
        Dec("margin.floor_pct", "ODDS", "P O", 0m, "Minimum margin (sanity)", 0, 0.30m),
        Dec("odds.min", "ODDS", "P O s t e", 1.01m, "Minimum odds offered", 1, 1000, mt: true),
        Dec("odds.max", "ODDS", "P O s t e", 1001m, "Maximum odds offered", 1, 10000, mt: true),
        Enum("odds.ladder", "ODDS", "O s", "std", ["std", "fine", "none"], "Odds ladder for rounding"),
        Dec("odds.override_feed_tolerance_pct", "ODDS", "O", 0.10m, "Alert when a manual override drifts this far from the feed", 0, 1),

        // LIM
        Money("limit.min_stake", "LIM", "P O s c t e m", 1m, "Minimum stake", mt: true, customer: "max"),
        Money("limit.max_stake", "LIM", "P O s c t e m", 5000m, "Maximum stake", mt: true, approval: true, customer: "min"),
        Money("limit.max_payout", "LIM", "P O s t", 100000m, "Maximum payout per ticket", approval: true, customer: "min"),
        Money("limit.max_liability_market", "LIM", "O s c t e m", null, "Maximum liability per market", mt: true, approval: true),
        Money("limit.max_liability_event", "LIM", "O s c t e", null, "Maximum liability per event", approval: true),
        Dec("limit.stake_factor", "LIM", "O s t", 1m, "Stake factor (combined with the customer / risk group factor)", 0, 100, customer: "multiply"),

        // BET
        Int("betdelay.live_sec", "BET", "O s c t e", 5, "Live bet delay in seconds", 0, 30, mt: true, customer: "max"),
        Int("betdelay.prematch_sec", "BET", "O s", 0, "Prematch bet delay in seconds", 0, 30, customer: "max"),
        Enum("bet.odds_change_policy", "BET", "O", "higher", ["none", "higher", "any"], "Default acceptance of odds changes"),
        Int("bet.combo.max_selections", "BET", "O s", 20, "Maximum selections on an accumulator", 1, 50),
        Bool("bet.combo.allow_same_event", "BET", "O s t", false, "Allow several selections from one event"),
        Bool("bet.system.enabled", "BET", "O", true, "System bets offered"),
        Enum("settlement.on_certainty", "BET", "P O s", "2", ["1", "2"], "Settle bets on certainty 1 (live scouted) or wait for 2 (confirmed)"),
        Money("settlement.manual_four_eyes_threshold", "BET", "O", 0m, "Manual settlements above this need a second approver"),

        // CASH
        Bool("cashout.enabled", "CASH", "P O s c t e m", false, "Cash-out available", Combine.AllPath, mt: true, approval: true),
        Dec("cashout.margin_pct", "CASH", "O s t", 0.05m, "Cash-out margin", 0, 0.5m, mt: true),
        Bool("cashout.partial_enabled", "CASH", "O s", false, "Partial cash-out"),
        Bool("cashout.auto_enabled", "CASH", "O s", false, "Auto cash-out"),
        Money("cashout.min_value", "CASH", "O", 1m, "Minimum cash-out value"),

        // MON / referral (docs/10 §3.1, decisions 2026-10-04)
        Bool("referral.enabled", "MON", "O s c t e", false, "Send matching bets to manual review", Combine.AllPath),
        Money("referral.stake_over", "MON", "O s c t e", null, "Refer tickets with stake above", mt: true, customer: "min"),
        Money("referral.potential_win_over", "MON", "O s c t e", null, "Refer tickets with potential win above", mt: true, customer: "min"),
        Dec("referral.total_odds_over", "MON", "O s t", null, "Refer tickets with total odds above", 1, customer: "min"),
        Dec("referral.liability_pct_over", "MON", "O s c t e", null, "Refer when liability utilisation after the bet exceeds", 0, 1, mt: true, customer: "min"),
        Money("referral.customer_event_stake_over", "MON", "O s t e", null, "Refer when the customer's cumulative stake on the event exceeds", customer: "min"),
        List("referral.customer_flags", "MON", "O s", [], "Refer every bet of customers with these flags"),
        List("referral.risk_groups", "MON", "O s", [], "Refer every bet of these risk groups"),
        Money("referral.new_customer_stake_over", "MON", "O s", null, "Refer bets of new customers above"),
        Int("referral.live_timeout_seconds", "MON", "O s t e", 30, "Review time for live bets", 5, 120),
        Int("referral.prematch_timeout_seconds", "MON", "O s t e", 180, "Review time for prematch bets", 10, 900),
        Enum("referral.on_timeout", "MON", "O s", "reject", ["reject", "accept"], "What happens when review time runs out"),
        Dec("referral.odds_drop_pct", "MON", "O s", 0.05m, "Auto-cancel when current odds drop below placed odds by", 0, 1),
        new("referral.on_market_suspend", "MON", ValueType.Json, S("O s"),
            new JsonObject { ["live"] = "cancel", ["prematch"] = "cancel" }, "Market closes during review: cancel the ticket"),
        Bool("referral.counter_offer_enabled", "MON", "O s", true, "Traders can send counter-offers"),
        Int("referral.counter_offer_timeout_seconds", "MON", "O s", 30, "Time the player has to answer a counter-offer", 10, 120),
        Dec("referral.partial_min_pct", "MON", "O", 0.10m, "Counter-offer stake must be at least this share of the requested stake", 0, 1),
        Int("referral.claim_ttl_seconds", "MON", "O", 30, "Claim lease", 10, 300),
        Int("referral.max_pending", "MON", "O", 200, "Queue size before the timeout policy applies at once", 1, 10000),
        Int("monitor.new_customer_days", "MON", "O", 7, "Customer counts as new for this many days", 0, 365),
        Int("monitor.ticker_retention_minutes", "MON", "P", 120, "Live ticker window", 10, 1440, operatorEditable: false),

        // PROMO / RG / I18N / misc
        Bool("promo.price_boost.enabled", "PROMO", "O s t", true, "Price boosts", Combine.AllPath),
        Bool("promo.freebet.enabled", "PROMO", "O s t", true, "Freebets usable", Combine.AllPath),
        Int("rg.min_age", "CUS", "P O", 25, "Minimum player age (Georgia: 25)", 18, 99, operatorEditable: false),
        new("i18n.default_lang", "I18N", ValueType.String, S("O"), JsonValue.Create("ka"), "Default language"),
        List("i18n.required_langs", "I18N", "O", ["ka", "en"], "Languages that must be translated"),
        List("i18n.fallback_langs", "I18N", "O", ["en"], "Fallback languages"),
        Bool("i18n.hide_unreviewed", "I18N", "O", false, "Hide machine / unreviewed translations"),
        Bool("manual.non_sport_allowed", "CAT", "P O", false, "Non-sport manual events allowed", operatorEditable: false),
        Enum("feed.producer_down_policy", "CAT", "P O s", "suspend", ["suspend", "hide"], "Offer behaviour while a feed producer is down"),
        Bool("cms.show_technical_codes", "CMS", "O", false, "Show technical codes next to customer messages"),
        Enum("wl.odds_decimal_separator", "WL", "O", ".", [".", ","], "Decimal separator in odds (2.50)"),
    ];

    public static readonly IReadOnlyDictionary<string, SettingDef> ByKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);
}
