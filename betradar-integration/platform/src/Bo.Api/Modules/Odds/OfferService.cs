using System.Text.Json.Nodes;
using Bo.Api.Infrastructure;
using Bo.Api.Modules.Catalog;
using Bo.Api.Modules.Config;
using Bo.Api.Modules.I18n;
using Bo.Core.Config;
using Dapper;
using Npgsql;
using Offer.Core;
using Platform.Canonical.Feed;

namespace Bo.Api.Modules.Odds;

public sealed record TradingDto(
    long Id, long? OperatorId, string ScopeType, long ScopeId, string Action, DateTime? ExpiresAt, string Reason,
    string? CreatedByName, DateTime CreatedAt)
{
    public bool Platform => OperatorId is null;
}

public sealed record OverrideDto(
    long Id, long MarketId, string OutcomeCode, string Kind, decimal Value, string ClearOn, decimal? FeedOddsAtSet,
    DateTime ExpiresAt, string Reason, string? CreatedByName, DateTime CreatedAt);

public sealed record OutcomeOffer(
    string Code, string Name, decimal? FeedOdds, decimal? Odds, decimal? FairProbability, string Source, bool Visible,
    string? HiddenReason, OverrideDto? Override, bool OverrideApplied);

public sealed record MarketOffer(
    long Id, int MarketTypeId, string MarketTypeCode, string Name, string Specifiers, bool IsManual, string FeedStatus,
    OfferStatus Status, IReadOnlyList<string> Reasons, string Mode, bool Complete, decimal? FeedOverround, decimal? OfferOverround,
    IReadOnlyList<TradingDto> Trading, IReadOnlyList<OutcomeOffer> Outcomes, PricingSettings Settings);

public sealed record EventHeader(
    long Id, string Name, string Status, bool Live, DateTime? ScheduledAt, long SportId, long? TournamentId, string? TournamentName,
    IReadOnlyList<string> Competitors);

public sealed record EventOffer(EventHeader Event, IReadOnlyList<TradingDto> Trading, IReadOnlyList<MarketOffer> Markets);

/// <summary>
/// The operator's offer for an event, as the player will see it: feed state from <c>sb</c> plus this operator's overlay
/// (CFG settings, trading and odds overrides, manual markets) priced by <see cref="OfferPricer"/> (docs/06 §4.2, §7.2).
/// The distribution API reuses the same inputs and the same library, so the trading view shows exactly what is offered.
/// </summary>
public sealed class OfferService(SettingsSnapshotCache settings, Names names)
{
    /// <summary>
    /// Markets an operator may see: every feed market, and manual markets owned by the operator or the platform
    /// (bo.manual_entity is row-level secured, so another operator's manual rows do not match). Alias the market <c>m</c>.
    /// </summary>
    public const string VisibleMarketSql =
        "(m.source_producer_id IS DISTINCT FROM 0 OR EXISTS (SELECT 1 FROM bo.manual_entity me WHERE me.entity_type = 'market' AND me.entity_id = m.id))";

    private static readonly string[] LiveStatuses = ["live", "interrupted", "suspended"];

    public static bool IsLive(string eventStatus) => LiveStatuses.Contains(eventStatus);

    public async Task<EventOffer> EventAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long operatorId, long eventId, string lang, long? marketId = null)
    {
        var e = await conn.QuerySingleOrDefaultAsync<EventSql>("""
            SELECT e.id, e.name_i18n::text AS nameI18n, e.status::text AS status, e.scheduled_at AS scheduledAt, e.sport_id::bigint AS sportId,
                   e.tournament_id::bigint AS tournamentId, t.name_i18n::text AS tournamentI18n
            FROM sb.event e LEFT JOIN sb.tournament t ON t.id = e.tournament_id WHERE e.id = @eventId
            """, new { eventId }, tx) ?? throw BoProblem.NotFound("Event");
        var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang);

        var competitorRows = (await conn.QueryAsync<(long Id, string NameI18n)>("""
            SELECT c.id, c.name_i18n::text FROM sb.event_competitor ec JOIN sb.competitor c ON c.id = ec.competitor_id
            WHERE ec.event_id = @eventId ORDER BY ec.position
            """, new { eventId }, tx)).ToList();
        var competitorNames = await names.ResolveAsync(conn, tx, "competitor", competitorRows.ToDictionary(c => c.Id.ToString(), c => (string?)c.NameI18n), langs);
        var competitors = competitorRows.Select(c => competitorNames[c.Id.ToString()].Text).ToList();
        var eventName = (await names.ResolveAsync(conn, tx, "event", new Dictionary<string, string?> { [e.Id.ToString()] = e.NameI18n }, langs))[e.Id.ToString()];
        var tournamentName = e.TournamentId is { } tid
            ? (await names.ResolveAsync(conn, tx, "tournament", new Dictionary<string, string?> { [tid.ToString()] = e.TournamentI18n }, langs))[tid.ToString()].Text
            : null;
        var live = IsLive(e.Status);
        var header = new EventHeader(e.Id, eventName.Source != "id" ? eventName.Text : competitors.Count > 0 ? string.Join(" v ", competitors) : $"#{e.Id}",
            e.Status, live, e.ScheduledAt, e.SportId, e.TournamentId, tournamentName, competitors);

        var markets = (await conn.QueryAsync<MarketSql>($"""
            SELECT m.id, m.market_description_id AS marketTypeId, d.code, d.name_template_i18n::text AS templateI18n,
                   coalesce((d.attributes->>'manual_of')::int, d.id) AS nameTypeId, d.outcome_kind::text AS outcomeKind,
                   d.is_enabled AS typeEnabled, m.specifiers, m.status::text AS status, m.feed_status::text AS feedStatus,
                   coalesce(m.source_producer_id = 0, false) AS isManual,
                   CASE WHEN d.is_variant THEN NULL ELSE nullif((SELECT count(*)::int FROM sb.market_description_outcome x
                        WHERE x.market_description_id = m.market_description_id AND x.variant = ''), 0) END AS definedOutcomes
            FROM sb.market m JOIN sb.market_description d ON d.id = m.market_description_id
            WHERE m.event_id = @eventId AND (@marketId::bigint IS NULL OR m.id = @marketId) AND {VisibleMarketSql}
            ORDER BY (m.status IN ('active', 'suspended')) DESC, d.id, m.specifiers
            """, new { eventId, marketId }, tx)).ToList();
        var ids = markets.Select(m => m.Id).ToArray();

        var outcomes = (await conn.QueryAsync<OutcomeSql>("""
            SELECT o.market_id AS marketId, o.code, o.odds, o.probability, o.is_active AS isActive, o.name_i18n::text AS nameI18n,
                   dmo.variant, dmo.name_template_i18n::text AS templateI18n
            FROM sb.outcome o LEFT JOIN sb.market_description_outcome dmo ON dmo.id = o.description_outcome_id
            WHERE o.market_id = ANY(@ids) ORDER BY o.market_id, coalesce(dmo.ordinal, 32767), o.code
            """, new { ids }, tx)).ToLookup(o => o.MarketId);

        var trading = (await conn.QueryAsync<TradingDto>("""
            SELECT id, operator_id AS operatorId, scope_type AS scopeType, scope_id AS scopeId, action, expires_at AS expiresAt, reason,
                   created_by_name AS createdByName, created_at AS createdAt
            FROM bo.trading_override
            WHERE cleared_at IS NULL AND (expires_at IS NULL OR expires_at > now())
              AND ((scope_type = 'event' AND scope_id = @eventId) OR (scope_type = 'market' AND scope_id = ANY(@ids)))
            ORDER BY id
            """, new { eventId, ids }, tx)).ToList();
        var overrides = (await conn.QueryAsync<OverrideDto>("""
            SELECT id, market_id AS marketId, outcome_code AS outcomeCode, kind, value, clear_on AS clearOn, feed_odds_at_set AS feedOddsAtSet,
                   expires_at AS expiresAt, reason, created_by_name AS createdByName, created_at AS createdAt
            FROM bo.odds_override WHERE cleared_at IS NULL AND market_id = ANY(@ids)
            """, new { ids }, tx)).ToLookup(o => o.MarketId);

        // Names: market and outcome templates in the operator's language (a manual market type uses its origin's texts).
        var marketTemplates = await names.ResolveAsync(conn, tx, "market_type",
            markets.DistinctBy(m => m.NameTypeId).ToDictionary(m => m.NameTypeId.ToString(), m => (string?)m.TemplateI18n), langs, "template");
        var outcomeKeys = markets.SelectMany(m => outcomes[m.Id].Where(o => o.TemplateI18n is not null)
                .Select(o => (Key: I18nEndpoints.OutcomeKey(m.NameTypeId, o.Variant ?? "", o.Code), o.TemplateI18n)))
            .DistinctBy(x => x.Key).ToDictionary(x => x.Key, x => x.TemplateI18n);
        var outcomeTemplates = await names.ResolveAsync(conn, tx, "outcome_type", outcomeKeys, langs, "template");

        var rows = await settings.GetAsync(conn, tx, operatorId);
        var path = await ScopePaths.BuildAsync(conn, tx, operatorId, new ScopeQuery(EventId: eventId));
        var eventTrading = trading.Where(x => x.ScopeType == "event").ToList();
        var now = DateTime.UtcNow;

        var result = markets.Select(m =>
        {
            var ctx = path with { MarketId = m.Id, MarketTypeId = m.MarketTypeId };
            var (pricing, gate) = Resolve(rows, ctx, live, m.TypeEnabled);
            var marketTrading = trading.Where(x => x.ScopeType == "market" && x.ScopeId == m.Id).ToList();
            var applies = eventTrading.Concat(marketTrading).ToList();
            gate = gate with
            {
                TradingClosed = applies.Any(x => x.Action == "close"),
                TradingSuspended = applies.Any(x => x.Action == "suspend"),
            };
            var marketOverrides = overrides[m.Id].ToList();
            var input = new MarketInput(m.Id, m.FeedStatus, m.Status,
                outcomes[m.Id].Select(o => new OutcomeInput(o.Code, o.Odds, o.Probability, o.IsActive)).ToList(),
                m.IsManual, m.OutcomeKind is "static" or "variant", m.DefinedOutcomes);
            var priced = OfferPricer.Price(input, pricing, gate,
                marketOverrides.Select(o => new OddsOverride(o.Id, o.OutcomeCode, o.Kind, o.Value, o.ExpiresAt, o.ClearOn, o.FeedOddsAtSet)).ToList(), now);

            var marketName = NameRenderer.Render(marketTemplates[m.NameTypeId.ToString()].Text, m.Specifiers, competitors);
            var byCode = outcomes[m.Id].ToDictionary(o => o.Code);
            var outcomeOffers = priced.Outcomes.Select(p =>
            {
                var source = byCode[p.Code];
                var name = source.NameI18n is not null && Names.Parse(source.NameI18n) is { Count: > 0 } dynamic
                    ? langs.Select(dynamic.GetValueOrDefault).FirstOrDefault(n => n is not null) ?? dynamic.Values.First()
                    : source.TemplateI18n is not null
                        ? NameRenderer.Render(outcomeTemplates[I18nEndpoints.OutcomeKey(m.NameTypeId, source.Variant ?? "", p.Code)].Text, m.Specifiers, competitors)
                        : p.Code;
                var ov = marketOverrides.FirstOrDefault(o => o.OutcomeCode == p.Code);
                return new OutcomeOffer(p.Code, name, p.FeedOdds, p.Odds, p.FairProbability, p.Source, p.Visible, p.HiddenReason, ov,
                    ov is not null && p.OverrideId == ov.Id);
            }).ToList();
            return new MarketOffer(m.Id, m.MarketTypeId, m.Code, marketName, m.Specifiers, m.IsManual, m.FeedStatus, priced.Status, priced.Reasons,
                priced.ModeUsed, priced.Complete, priced.FeedOverround, priced.OfferOverround, marketTrading, outcomeOffers, pricing);
        }).ToList();
        return new EventOffer(header, eventTrading, result);
    }

    /// <summary>Effective pricing settings and policy flags for one market (docs/06 §5.8).</summary>
    public static (PricingSettings Pricing, MarketGate Gate) Resolve(IReadOnlyList<SettingRow> rows, ScopeContext ctx, bool live, bool typeEnabledOnPlatform)
    {
        JsonNode? V(string key) => SettingResolver.Resolve(SettingCatalog.ByKey[key], rows, ctx).Value;
        string S(string key) => V(key)!.GetValue<string>();
        decimal D(string key) => JsonNumbers.Decimal(V(key)!);
        bool B(string key) => V(key)!.GetValue<bool>();

        var pricing = new PricingSettings(
            S("margin.mode"), D("margin.pct"), D("margin.delta_pct"), S("margin.method"), S("margin.remove_method"),
            B("margin.use_feed_probabilities"), D("margin.floor_pct"), D("odds.min"), D("odds.max"), S("odds.ladder"),
            D("odds.override_feed_tolerance_pct"));
        var gate = new MarketGate(
            Visible: B("offer.visible"),
            MarketTypeEnabled: typeEnabledOnPlatform && B("market.enabled"),
            PhaseEnabled: B(live ? "offer.live_enabled" : "offer.prematch_enabled"),
            ProducerDownPolicy: S("feed.producer_down_policy"),
            EventLive: live,
            ManualMarketLive: B("manual.market_live"));
        return (pricing, gate);
    }

    /// <summary>Longest override allowed now (CFG odds.override_max_ttl_min, live or prematch).</summary>
    public static int MaxOverrideMinutes(IReadOnlyList<SettingRow> rows, ScopeContext ctx, bool live)
    {
        var value = SettingResolver.Resolve(SettingCatalog.ByKey["odds.override_max_ttl_min"], rows, ctx).Value;
        return value?[live ? "live" : "prematch"] is { } minutes ? (int)JsonNumbers.Decimal(minutes) : live ? 120 : 1440;
    }

    private sealed class EventSql
    {
        public long Id { get; init; }
        public string? NameI18n { get; init; }
        public string Status { get; init; } = "";
        public DateTime? ScheduledAt { get; init; }
        public long SportId { get; init; }
        public long? TournamentId { get; init; }
        public string? TournamentI18n { get; init; }
    }

    private sealed class MarketSql
    {
        public long Id { get; init; }
        public int MarketTypeId { get; init; }
        public string Code { get; init; } = "";
        public string TemplateI18n { get; init; } = "{}";
        public int NameTypeId { get; init; }
        public string OutcomeKind { get; init; } = "";
        public bool TypeEnabled { get; init; }
        public string Specifiers { get; init; } = "";
        public string Status { get; init; } = "";
        public string FeedStatus { get; init; } = "";
        public bool IsManual { get; init; }
        public int? DefinedOutcomes { get; init; }
    }

    private sealed class OutcomeSql
    {
        public long MarketId { get; init; }
        public string Code { get; init; } = "";
        public decimal? Odds { get; init; }
        public decimal? Probability { get; init; }
        public bool IsActive { get; init; }
        public string? NameI18n { get; init; }
        public string? Variant { get; init; }
        public string? TemplateI18n { get; init; }
    }
}
