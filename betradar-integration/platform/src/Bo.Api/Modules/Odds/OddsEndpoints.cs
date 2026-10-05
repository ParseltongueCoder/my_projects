using System.Globalization;
using System.Text.Json.Nodes;
using Bo.Api.Infrastructure;
using Bo.Api.Modules.Catalog;
using Bo.Api.Modules.Config;
using Bo.Core.Config;
using Dapper;
using Npgsql;
using Offer.Core;
using Platform.Canonical.Feed;

namespace Bo.Api.Modules.Odds;

/// <summary>
/// ODDS (docs/06 §4): trading view (feed vs offered prices), manual odds overrides with a TTL, suspend / close of
/// events and markets, manual markets from a market type template, the market type enable matrix (CFG
/// <c>market.enabled</c>) and the margin simulator. Feed rows are never changed; manual markets are the operator's own
/// sb rows (docs/09 §2.4), guarded by row-level security (V007).
/// </summary>
public static class OddsEndpoints
{
    public sealed record OverrideRequest(long MarketId, string OutcomeCode, string Kind, decimal Value, string? ClearOn,
        int? TtlMinutes, DateTime? ExpiresAt, string Reason);

    public sealed record TradingRequest(string ScopeType, long ScopeId, string Action, int? TtlMinutes, DateTime? ExpiresAt,
        string Reason, bool Platform = false);

    public sealed record ManualOutcome(string Code, decimal? Odds, bool? IsActive);

    public sealed record ManualMarketRequest(int MarketTypeId, string? Specifiers, IReadOnlyList<ManualOutcome> Outcomes, string? Status, string? Reason);

    public sealed record ManualMarketPatch(IReadOnlyList<ManualOutcome>? Outcomes, string? Status, string? Reason);

    public sealed record MarketTypeToggle(long? SportId, bool Enabled, string Reason);

    public sealed record SimOutcome(string Code, decimal Odds);

    public sealed record SimSettings(string? Mode, decimal? Pct, decimal? DeltaPct, string? Method, string? RemoveMethod,
        decimal? MinOdds, decimal? MaxOdds, string? Ladder, decimal? FloorPct);

    public sealed record SimulateRequest(long? MarketId, IReadOnlyList<SimOutcome>? Outcomes, bool? ClosedSet, SimSettings? Settings);

    private static readonly string[] ManualStatuses = ["active", "suspended", "deactivated"];
    private static readonly string[] OpenEventStatuses = ["not_started", "live", "suspended", "interrupted", "delayed"];

    public static RouteGroupBuilder MapOdds(this RouteGroupBuilder api)
    {
        var odds = api.MapGroup("/odds");

        odds.MapGet("/events/{id:long}/markets", (TenantContext t, BoDb db, OfferService offer, long id, string? lang, CancellationToken ct) =>
        {
            t.Require("odds.view");
            var operatorId = t.RequireOperator();
            return db.TenantAsync(t, (conn, tx) => offer.EventAsync(conn, tx, operatorId, id, lang ?? "ka"), ct);
        });

        // ---------------------------------------------------------------- odds overrides
        odds.MapGet("/overrides", (TenantContext t, BoDb db, Names names, long? eventId, string? lang, CancellationToken ct) =>
        {
            t.Require("odds.view");
            var operatorId = t.RequireOperator();
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var rows = (await conn.QueryAsync<OverrideListRow>("""
                    SELECT o.id, o.market_id AS marketId, m.event_id AS eventId, o.outcome_code AS outcomeCode, o.kind, o.value, o.clear_on AS clearOn,
                           o.feed_odds_at_set AS feedOddsAtSet, oc.odds AS feedOdds, o.expires_at AS expiresAt, o.reason,
                           o.created_by_name AS createdByName, o.created_at AS createdAt, d.code AS marketTypeCode, m.specifiers
                    FROM bo.odds_override o
                    JOIN sb.market m ON m.id = o.market_id JOIN sb.market_description d ON d.id = m.market_description_id
                    LEFT JOIN sb.outcome oc ON oc.market_id = o.market_id AND oc.code = o.outcome_code
                    WHERE o.cleared_at IS NULL AND (@eventId::bigint IS NULL OR m.event_id = @eventId)
                    ORDER BY o.expires_at
                    """, new { eventId }, tx)).ToList();
                var events = await EventNamesAsync(conn, tx, names, operatorId, lang ?? "ka", rows.Select(r => r.EventId).Distinct().ToArray());
                return rows.Select(r => new { r.Id, r.MarketId, r.EventId, eventName = events.GetValueOrDefault(r.EventId), r.MarketTypeCode, r.Specifiers,
                    r.OutcomeCode, r.Kind, r.Value, r.ClearOn, r.FeedOddsAtSet, r.FeedOdds, r.ExpiresAt, r.Reason, r.CreatedByName, r.CreatedAt });
            }, ct);
        });

        odds.MapPost("/overrides", (TenantContext t, BoDb db, OfferService offer, SettingsSnapshotCache settings, OverrideRequest body, CancellationToken ct) =>
        {
            t.Require("odds.override");
            var operatorId = t.RequireOperator();
            RequireReason(body.Reason);
            if (body.Kind is not ("absolute" or "shift_pct"))
            {
                throw BoProblem.Invalid("BAD_KIND", "kind must be 'absolute' or 'shift_pct'");
            }
            if (body.Kind == "absolute" && body.Value is < 1.01m or > 10000m)
            {
                throw BoProblem.Invalid("BAD_ODDS", "Odds must be between 1.01 and 10000");
            }
            if (body.Kind == "shift_pct" && (body.Value is < -0.5m or > 0.5m || body.Value == 0))
            {
                throw BoProblem.Invalid("BAD_SHIFT", "A shift is a non-zero fraction between -0.5 and 0.5 (e.g. -0.05 = 5% lower)");
            }
            var clearOn = body.ClearOn ?? "expiry";
            if (clearOn is not ("expiry" or "feed_change"))
            {
                throw BoProblem.Invalid("BAD_CLEAR_ON", "clearOn must be 'expiry' or 'feed_change'");
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var market = await MarketAsync(conn, tx, body.MarketId);
                if (market.IsManual)
                {
                    throw BoProblem.Invalid("MANUAL_MARKET", "Edit the prices of a manual market directly");
                }
                if (market.Status is "settled" or "cancelled")
                {
                    throw BoProblem.Conflict("MARKET_CLOSED", $"The market is {market.Status}");
                }
                var feedOdds = await conn.QuerySingleOrDefaultAsync<(bool Found, decimal? Odds)>(
                    "SELECT true, odds FROM sb.outcome WHERE market_id = @MarketId AND code = @OutcomeCode", new { body.MarketId, body.OutcomeCode }, tx);
                if (!feedOdds.Found)
                {
                    throw BoProblem.Invalid("UNKNOWN_OUTCOME", $"Outcome '{body.OutcomeCode}' is not in this market");
                }

                var live = OfferService.IsLive(market.EventStatus);
                var rows = await settings.GetAsync(conn, tx, operatorId);
                var ctx = await ScopePaths.BuildAsync(conn, tx, operatorId, new ScopeQuery(MarketId: body.MarketId));
                var maxMinutes = OfferService.MaxOverrideMinutes(rows, ctx, live);
                var expiresAt = Expiry(body.TtlMinutes, body.ExpiresAt, required: true)!.Value;
                if (expiresAt > DateTime.UtcNow.AddMinutes(maxMinutes).AddSeconds(5))
                {
                    throw BoProblem.Invalid("TTL_TOO_LONG", $"Overrides last at most {maxMinutes} minutes {(live ? "live" : "prematch")} (odds.override_max_ttl_min)");
                }

                // One active override per outcome: a new one replaces it.
                var replaced = await conn.QueryAsync<long>("""
                    UPDATE bo.odds_override SET cleared_at = now(), cleared_by = @user, cleared_by_name = @name, clear_reason = 'replaced'
                    WHERE market_id = @MarketId AND outcome_code = @OutcomeCode AND cleared_at IS NULL RETURNING id
                    """, new { body.MarketId, body.OutcomeCode, user = t.UserId, name = t.ActorName }, tx);
                var created = await conn.QuerySingleAsync<OverrideDto>("""
                    INSERT INTO bo.odds_override (operator_id, market_id, outcome_code, kind, value, clear_on, feed_odds_at_set, expires_at,
                                                  reason, created_by, created_by_name)
                    VALUES (@operatorId, @MarketId, @OutcomeCode, @Kind, @Value, @clearOn, @feed, @expiresAt, @reason, @user, @name)
                    RETURNING id, market_id AS marketId, outcome_code AS outcomeCode, kind, value, clear_on AS clearOn, feed_odds_at_set AS feedOddsAtSet,
                              expires_at AS expiresAt, reason, created_by_name AS createdByName, created_at AS createdAt
                    """, new
                {
                    operatorId, body.MarketId, body.OutcomeCode, body.Kind, body.Value, clearOn, feed = feedOdds.Odds, expiresAt,
                    reason = body.Reason.Trim(), user = t.UserId, name = t.ActorName,
                }, tx);
                await Audit.WriteAsync(conn, tx, t, "odds.override.created", "odds_override", created.Id.ToString(),
                    replaced.Any() ? new { replaced } : null, created, body.Reason);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId}.odds", new { market.EventId, body.MarketId });

                var warnings = new List<string>();
                var tolerance = JsonNumbers.Decimal(SettingResolver.Resolve(SettingCatalog.ByKey["odds.override_feed_tolerance_pct"], rows, ctx).Value!);
                if (body.Kind == "absolute" && feedOdds.Odds is { } f && f > 0 && Math.Abs(body.Value - f) / f > tolerance)
                {
                    warnings.Add($"The override is {Math.Abs(body.Value - f) / f:P0} away from the feed price {f.ToString("0.00", CultureInfo.InvariantCulture)}");
                }
                if (live)
                {
                    warnings.Add("Live event: the price changes for bets after the live bet delay");
                }
                var after = await offer.EventAsync(conn, tx, operatorId, market.EventId, "en", body.MarketId);
                return Results.Created($"/api/bo/odds/overrides/{created.Id}", new { @override = created, warnings, market = after.Markets.SingleOrDefault() });
            }, ct);
        });

        odds.MapDelete("/overrides/{id:long}", (TenantContext t, BoDb db, long id, string? reason, CancellationToken ct) =>
        {
            t.Require("odds.override");
            var operatorId = t.RequireOperator();
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var cleared = await conn.QuerySingleOrDefaultAsync<(long MarketId, long EventId)>("""
                    UPDATE bo.odds_override o SET cleared_at = now(), cleared_by = @user, cleared_by_name = @name, clear_reason = @reason
                    FROM sb.market m WHERE o.id = @id AND o.cleared_at IS NULL AND m.id = o.market_id
                    RETURNING o.market_id, m.event_id
                    """, new { id, user = t.UserId, name = t.ActorName, reason = reason ?? "cleared" }, tx);
                if (cleared == default)
                {
                    throw BoProblem.NotFound("Active override");
                }
                await Audit.WriteAsync(conn, tx, t, "odds.override.cleared", "odds_override", id.ToString(), reason: reason);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId}.odds", new { cleared.EventId, cleared.MarketId });
                return Results.NoContent();
            }, ct);
        });

        // ---------------------------------------------------------------- suspend / close
        odds.MapGet("/trading", (TenantContext t, BoDb db, Names names, long? eventId, string? lang, CancellationToken ct) =>
        {
            t.Require("odds.view");
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var rows = (await conn.QueryAsync<TradingListRow>("""
                    SELECT o.id, o.operator_id AS operatorId, o.scope_type AS scopeType, o.scope_id AS scopeId, o.action, o.expires_at AS expiresAt,
                           o.reason, o.created_by_name AS createdByName, o.created_at AS createdAt,
                           coalesce(m.event_id, CASE WHEN o.scope_type = 'event' THEN o.scope_id END) AS eventId,
                           d.code AS marketTypeCode, m.specifiers
                    FROM bo.trading_override o
                    LEFT JOIN sb.market m ON o.scope_type = 'market' AND m.id = o.scope_id
                    LEFT JOIN sb.market_description d ON d.id = m.market_description_id
                    WHERE o.cleared_at IS NULL AND (o.expires_at IS NULL OR o.expires_at > now())
                      AND (@eventId::bigint IS NULL OR coalesce(m.event_id, CASE WHEN o.scope_type = 'event' THEN o.scope_id END) = @eventId)
                    ORDER BY o.created_at DESC
                    """, new { eventId }, tx)).ToList();
                var events = t.OperatorId is { } op
                    ? await EventNamesAsync(conn, tx, names, op, lang ?? "ka", rows.Select(r => r.EventId).Distinct().ToArray())
                    : [];
                return rows.Select(r => new { r.Id, r.OperatorId, platform = r.OperatorId is null, r.ScopeType, r.ScopeId, r.EventId,
                    eventName = events.GetValueOrDefault(r.EventId), r.MarketTypeCode, r.Specifiers, r.Action, r.ExpiresAt, r.Reason, r.CreatedByName, r.CreatedAt });
            }, ct);
        });

        odds.MapPost("/trading", (TenantContext t, BoDb db, TradingRequest body, CancellationToken ct) =>
        {
            t.Require("odds.suspend");
            long? operatorId;
            if (body.Platform)
            {
                // Our incident response: every operator at once.
                t.RequirePlatform();
                t.Require(Bo.Core.Security.Permissions.PlatformSettingsEdit);
                operatorId = null;
            }
            else
            {
                operatorId = t.RequireOperator();
            }
            RequireReason(body.Reason);
            if (body.ScopeType is not ("event" or "market"))
            {
                throw BoProblem.Invalid("BAD_SCOPE", "scopeType must be 'event' or 'market'");
            }
            if (body.Action is not ("suspend" or "close"))
            {
                throw BoProblem.Invalid("BAD_ACTION", "action must be 'suspend' or 'close'");
            }
            var expiresAt = Expiry(body.TtlMinutes, body.ExpiresAt, required: false);
            return db.TenantAsync(t, async (conn, tx) =>
            {
                long eventId;
                if (body.ScopeType == "event")
                {
                    eventId = await conn.ExecuteScalarAsync<long?>("SELECT id FROM sb.event WHERE id = @ScopeId", new { body.ScopeId }, tx)
                              ?? throw BoProblem.NotFound("Event");
                }
                else
                {
                    eventId = (await MarketAsync(conn, tx, body.ScopeId)).EventId;
                }
                var created = await conn.QuerySingleAsync<TradingDto>("""
                    INSERT INTO bo.trading_override (operator_id, scope_type, scope_id, action, expires_at, reason, created_by, created_by_name)
                    VALUES (@operatorId, @ScopeType, @ScopeId, @Action, @expiresAt, @reason, @user, @name)
                    RETURNING id, operator_id AS operatorId, scope_type AS scopeType, scope_id AS scopeId, action, expires_at AS expiresAt, reason,
                              created_by_name AS createdByName, created_at AS createdAt
                    """, new { operatorId, body.ScopeType, body.ScopeId, body.Action, expiresAt, reason = body.Reason.Trim(), user = t.UserId, name = t.ActorName }, tx);
                await Audit.WriteAsync(conn, tx, t, $"odds.trading.{body.Action}", body.ScopeType, body.ScopeId.ToString(), after: created,
                    reason: body.Reason, operatorId: operatorId);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId?.ToString() ?? "platform"}.trading",
                    new { eventId, body.ScopeType, body.ScopeId, body.Action });
                return Results.Created($"/api/bo/odds/trading/{created.Id}", created);
            }, ct);
        });

        odds.MapDelete("/trading/{id:long}", (TenantContext t, BoDb db, long id, string? reason, CancellationToken ct) =>
        {
            t.Require("odds.suspend");
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var row = await conn.QuerySingleOrDefaultAsync<(long? OperatorId, string ScopeType, long ScopeId)>(
                    "SELECT operator_id, scope_type, scope_id FROM bo.trading_override WHERE id = @id AND cleared_at IS NULL", new { id }, tx);
                if (row == default)
                {
                    throw BoProblem.NotFound("Active suspension");
                }
                if (row.OperatorId is null)
                {
                    // A platform suspension is lifted by platform staff only (operators see it, RLS keeps it read-only for them).
                    t.RequirePlatform();
                }
                if (await conn.ExecuteAsync("""
                        UPDATE bo.trading_override SET cleared_at = now(), cleared_by = @user, cleared_by_name = @name, clear_reason = @reason
                        WHERE id = @id AND cleared_at IS NULL
                        """, new { id, user = t.UserId, name = t.ActorName, reason = reason ?? "lifted" }, tx) == 0)
                {
                    throw BoProblem.NotFound("Active suspension");
                }
                await Audit.WriteAsync(conn, tx, t, "odds.trading.cleared", row.ScopeType, row.ScopeId.ToString(), reason: reason, operatorId: row.OperatorId);
                await Audit.OutboxAsync(conn, tx, row.OperatorId, $"bo.changed.{row.OperatorId?.ToString() ?? "platform"}.trading",
                    new { row.ScopeType, row.ScopeId, action = "cleared" });
                return Results.NoContent();
            }, ct);
        });

        // ---------------------------------------------------------------- manual markets
        odds.MapPost("/events/{id:long}/manual-markets", (TenantContext t, BoDb db, OfferService offer, long id, ManualMarketRequest body, CancellationToken ct) =>
        {
            t.Require("cat.market.add_manual");
            t.Require("odds.view");
            var operatorId = t.RequireOperator();
            var status = body.Status ?? "active";
            if (!ManualStatuses.Contains(status))
            {
                throw BoProblem.Invalid("BAD_STATUS", "status must be active, suspended or deactivated");
            }
            if (body.Outcomes.Count == 0)
            {
                throw BoProblem.Invalid("OUTCOMES_REQUIRED", "Price at least one outcome");
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var eventStatus = await conn.ExecuteScalarAsync<string?>("SELECT status::text FROM sb.event WHERE id = @id", new { id }, tx)
                                  ?? throw BoProblem.NotFound("Event");
                if (!OpenEventStatuses.Contains(eventStatus))
                {
                    throw BoProblem.Conflict("EVENT_CLOSED", $"The event is {eventStatus}");
                }
                var template = await conn.QuerySingleOrDefaultAsync<(int Id, string Code, bool IsVariant)>(
                    "SELECT id, code, is_variant FROM sb.market_description WHERE id = @MarketTypeId", new { body.MarketTypeId }, tx);
                if (template == default || template.Code.StartsWith("manual:", StringComparison.Ordinal))
                {
                    throw BoProblem.Invalid("UNKNOWN_MARKET_TYPE", "Choose a feed market type as the template");
                }
                if (template.IsVariant)
                {
                    throw BoProblem.Invalid("VARIANT_MARKET_TYPE", "Variant market types cannot be used as manual templates yet");
                }
                var specifiers = await SpecifiersAsync(conn, tx, template.Id, body.Specifiers);
                var defined = (await conn.QueryAsync<string>(
                    "SELECT code FROM sb.market_description_outcome WHERE market_description_id = @Id AND variant = ''", new { template.Id }, tx)).ToHashSet();
                ValidateOutcomes(body.Outcomes, defined, creating: true);

                var copyId = await ManualTypeAsync(conn, tx, operatorId, template.Id, template.Code);
                var marketId = await conn.ExecuteScalarAsync<long>("""
                    INSERT INTO sb.market (event_id, market_description_id, specifiers, specifiers_json, status, feed_status, source_producer_id, last_feed_ts)
                    VALUES (@id, @copyId, @spec, @json::jsonb, @status::sb.market_status, @status::sb.market_status, 0, now()) RETURNING id
                    """, new { id, copyId, spec = specifiers.Normalized, json = specifiers.Json.ToJsonString(), status }, tx);
                await conn.ExecuteAsync("""
                    INSERT INTO bo.manual_entity (entity_type, entity_id, operator_id, meta, created_by)
                    VALUES ('market', @marketId, @operatorId, @meta::jsonb, @user)
                    """, new { marketId, operatorId, meta = new JsonObject { ["fromMarketTypeId"] = template.Id }.ToJsonString(), user = t.UserId }, tx);
                foreach (var o in body.Outcomes)
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO sb.outcome (market_id, code, description_outcome_id, odds, is_active, odds_updated_at)
                        VALUES (@marketId, @Code, (SELECT id FROM sb.market_description_outcome WHERE market_description_id = @copyId AND variant = '' AND code = @Code),
                                @Odds, @active, now())
                        """, new { marketId, o.Code, copyId, o.Odds, active = (o.IsActive ?? true) && o.Odds is not null }, tx);
                }
                await Audit.WriteAsync(conn, tx, t, "odds.manual_market.created", "market", marketId.ToString(),
                    after: new { eventId = id, marketTypeId = template.Id, specifiers = specifiers.Normalized, status, body.Outcomes }, reason: body.Reason);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId}.odds", new { eventId = id, marketId });
                var created = (await offer.EventAsync(conn, tx, operatorId, id, "en", marketId)).Markets.Single();
                return Results.Created($"/api/bo/odds/manual-markets/{marketId}", created);
            }, ct);
        });

        odds.MapPatch("/manual-markets/{id:long}", (TenantContext t, BoDb db, OfferService offer, long id, ManualMarketPatch body, CancellationToken ct) =>
        {
            t.Require("cat.market.add_manual");
            var operatorId = t.RequireOperator();
            if (body.Status is { } s && !ManualStatuses.Contains(s))
            {
                throw BoProblem.Invalid("BAD_STATUS", "status must be active, suspended or deactivated");
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                // Only manual markets this operator owns (bo.manual_entity is row-level secured).
                var market = await conn.QuerySingleOrDefaultAsync<(long EventId, string Status)>("""
                    SELECT m.event_id, m.status::text FROM sb.market m
                    JOIN bo.manual_entity me ON me.entity_type = 'market' AND me.entity_id = m.id
                    WHERE m.id = @id AND m.source_producer_id = 0 FOR UPDATE OF m
                    """, new { id }, tx);
                if (market == default)
                {
                    throw BoProblem.NotFound("Manual market");
                }
                if (market.Status is "settled" or "cancelled")
                {
                    throw BoProblem.Conflict("MARKET_CLOSED", $"The market is {market.Status}");
                }
                var before = (await offer.EventAsync(conn, tx, operatorId, market.EventId, "en", id)).Markets.Single();
                if (body.Outcomes is { Count: > 0 } outcomes)
                {
                    ValidateOutcomes(outcomes, before.Outcomes.Select(o => o.Code).ToHashSet(), creating: false);
                    foreach (var o in outcomes)
                    {
                        await conn.ExecuteAsync("""
                            UPDATE sb.outcome SET odds = coalesce(@Odds, odds), is_active = coalesce(@IsActive, is_active), odds_updated_at = now()
                            WHERE market_id = @id AND code = @Code
                            """, new { id, o.Code, o.Odds, o.IsActive }, tx);
                    }
                }
                if (body.Status is { } status)
                {
                    await conn.ExecuteAsync("""
                        UPDATE sb.market SET status = @status::sb.market_status, feed_status = @status::sb.market_status, last_feed_ts = now(),
                          version = version + 1, updated_at = now() WHERE id = @id
                        """, new { id, status }, tx);
                }
                else
                {
                    await conn.ExecuteAsync("UPDATE sb.market SET version = version + 1, updated_at = now() WHERE id = @id", new { id }, tx);
                }
                var after = (await offer.EventAsync(conn, tx, operatorId, market.EventId, "en", id)).Markets.Single();
                await Audit.WriteAsync(conn, tx, t, "odds.manual_market.updated", "market", id.ToString(),
                    new { before.FeedStatus, outcomes = before.Outcomes.Select(o => new { o.Code, o.FeedOdds }) },
                    new { after.FeedStatus, outcomes = after.Outcomes.Select(o => new { o.Code, o.FeedOdds }) }, body.Reason);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId}.odds", new { market.EventId, marketId = id });
                return after;
            }, ct);
        });

        // ---------------------------------------------------------------- market type matrix (CFG market.enabled)
        odds.MapGet("/market-types", (TenantContext t, BoDb db, Names names, SettingsSnapshotCache settings, string? q, string? lang, CancellationToken ct) =>
        {
            t.Require("odds.view");
            var operatorId = t.RequireOperator();
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var types = (await conn.QueryAsync<(int Id, string Code, string NameI18n, string[] Groups, bool IsEnabled)>("""
                    SELECT id, code, name_template_i18n::text, groups, is_enabled FROM sb.market_description
                    WHERE code NOT LIKE 'manual:%' AND (@q::text IS NULL OR code ILIKE '%' || @q || '%' OR name_template_i18n::text ILIKE '%' || @q || '%' OR id::text = @q)
                    ORDER BY id LIMIT 300
                    """, new { q = string.IsNullOrWhiteSpace(q) ? null : q.Trim() }, tx)).ToList();
                var sports = (await conn.QueryAsync<(long Id, string NameI18n)>("SELECT id::bigint, name_i18n::text FROM sb.sport ORDER BY id", transaction: tx)).ToList();
                var open = (await conn.QueryAsync<(int TypeId, long SportId, int Count)>($"""
                    SELECT m.market_description_id, e.sport_id::bigint, count(*)::int FROM sb.market m JOIN sb.event e ON e.id = m.event_id
                    WHERE m.status IN ('active', 'suspended') AND {OfferService.VisibleMarketSql} GROUP BY 1, 2
                    """, transaction: tx)).ToDictionary(x => (x.TypeId, x.SportId), x => x.Count);
                var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang ?? "ka");
                var typeNames = await names.ResolveAsync(conn, tx, "market_type", types.ToDictionary(x => x.Id.ToString(), x => (string?)x.NameI18n), langs, "template");
                var sportNames = await names.ResolveAsync(conn, tx, "sport", sports.ToDictionary(x => x.Id.ToString(), x => (string?)x.NameI18n), langs);
                var rows = await settings.GetAsync(conn, tx, operatorId);
                var def = SettingCatalog.ByKey["market.enabled"];

                object Cell(int typeId, ScopeType scope, long? sportId)
                {
                    var ctx = new ScopeContext(operatorId, SportId: sportId, MarketTypeId: typeId);
                    var effective = SettingResolver.Resolve(def, rows, ctx);
                    var here = rows.FirstOrDefault(r => r.Key == def.Key && r.OperatorId == operatorId && r.ScopeType == scope
                                                        && r.ScopeId == sportId && r.MarketTypeId == typeId);
                    return new { enabled = effective.Value!.GetValue<bool>(), setHere = here?.Value.GetValue<bool>() };
                }

                return new
                {
                    sports = sports.Select(s => new { s.Id, name = sportNames[s.Id.ToString()].Text }),
                    items = types.Select(x =>
                    {
                        var ctx = new ScopeContext(operatorId, MarketTypeId: x.Id);
                        return new
                        {
                            x.Id,
                            x.Code,
                            name = typeNames[x.Id.ToString()].Text,
                            x.Groups,
                            platformEnabled = x.IsEnabled,
                            operatorCell = Cell(x.Id, ScopeType.Operator, null),
                            sportCells = sports.ToDictionary(s => s.Id.ToString(), s => Cell(x.Id, ScopeType.Sport, s.Id)),
                            openMarkets = sports.ToDictionary(s => s.Id.ToString(), s => open.GetValueOrDefault((x.Id, s.Id))),
                            marginMode = SettingResolver.Resolve(SettingCatalog.ByKey["margin.mode"], rows, ctx).Value,
                            marginPct = SettingResolver.Resolve(SettingCatalog.ByKey["margin.pct"], rows, ctx).Value,
                        };
                    }),
                };
            }, ct);
        });

        // Disabling writes market.enabled=false here; enabling removes it (inherits again). It is a CFG change set.
        odds.MapPut("/market-types/{id:int}", async (TenantContext t, BoDb db, ConfigService cfg, SettingsSnapshotCache settings, int id, MarketTypeToggle body, CancellationToken ct) =>
        {
            t.Require("odds.view");
            var operatorId = t.RequireOperator();
            RequireReason(body.Reason);
            var setHere = await db.TenantAsync(t, async (conn, tx) =>
                (await settings.GetAsync(conn, tx, operatorId)).Any(r => r.Key == "market.enabled" && r.OperatorId == operatorId
                    && r.ScopeType == (body.SportId is null ? ScopeType.Operator : ScopeType.Sport) && r.ScopeId == body.SportId && r.MarketTypeId == id), ct);
            if (body.Enabled && !setHere)
            {
                throw BoProblem.Invalid("INHERITED", "Not disabled at this level: it is switched off higher up (operator or platform)");
            }
            var scope = body.SportId is null ? "operator" : "sport";
            var change = new SettingChangeRequest(body.Enabled ? "delete" : "upsert", scope, body.SportId, id, "market.enabled",
                body.Enabled ? null : JsonValue.Create(false));
            return await cfg.CreateAsync(t, new ChangeSetRequest(
                $"{(body.Enabled ? "Enable" : "Disable")} market type #{id}{(body.SportId is { } s ? $" for sport #{s}" : "")}: {body.Reason.Trim()}", false, [change]), ct);
        });

        // ---------------------------------------------------------------- simulator
        odds.MapPost("/simulate", (TenantContext t, BoDb db, OfferService offer, SettingsSnapshotCache settings, SimulateRequest body, CancellationToken ct) =>
        {
            t.Require("odds.view");
            var operatorId = t.RequireOperator();
            return db.TenantAsync(t, async (conn, tx) =>
            {
                PricingSettings baseSettings;
                MarketInput input;
                MarketOffer? current = null;
                if (body.MarketId is { } marketId)
                {
                    var market = await MarketAsync(conn, tx, marketId);
                    current = (await offer.EventAsync(conn, tx, operatorId, market.EventId, "en", marketId)).Markets.Single();
                    baseSettings = current.Settings;
                    input = new MarketInput(marketId, "active", "active",
                        current.Outcomes.Select(o => new OutcomeInput(o.Code, o.FeedOdds, null, o.FeedOdds is > 1)).ToList(),
                        current.IsManual, current.Complete || body.ClosedSet == true);
                }
                else
                {
                    if (body.Outcomes is not { Count: >= 1 and <= 100 } outcomes || outcomes.Any(o => o.Odds <= 1))
                    {
                        throw BoProblem.Invalid("OUTCOMES_REQUIRED", "Give 1-100 outcomes with odds above 1");
                    }
                    var rows = await settings.GetAsync(conn, tx, operatorId);
                    baseSettings = OfferService.Resolve(rows, new ScopeContext(operatorId), false, true).Pricing;
                    input = new MarketInput(0, "active", "active", outcomes.Select(o => new OutcomeInput(o.Code, o.Odds)).ToList(),
                        ClosedSet: body.ClosedSet ?? true);
                }
                var used = Merge(baseSettings, body.Settings);
                var priced = OfferPricer.Price(input, used, new MarketGate(), [], DateTime.UtcNow);
                return new { settings = used, priced, current };
            }, ct);
        });

        return api;
    }

    private static PricingSettings Merge(PricingSettings s, SimSettings? o)
    {
        if (o is null)
        {
            return s;
        }
        if (o.Mode is { } mode && mode is not ("feed" or "target" or "delta"))
        {
            throw BoProblem.Invalid("BAD_MODE", "mode must be feed, target or delta");
        }
        if ((o.Method is { } m && !Margin.Methods.Contains(m)) || (o.RemoveMethod is { } r && !Margin.Methods.Contains(r)))
        {
            throw BoProblem.Invalid("BAD_METHOD", $"method must be one of {string.Join(", ", Margin.Methods)}");
        }
        if (o.Ladder is { } l && !OddsLadder.Codes.Contains(l))
        {
            throw BoProblem.Invalid("BAD_LADDER", $"ladder must be one of {string.Join(", ", OddsLadder.Codes)}");
        }
        if (o.Pct is < 0 or > 0.30m || o.DeltaPct is < 0 or > 0.20m || o.FloorPct is < 0 or > 0.30m)
        {
            throw BoProblem.Invalid("BAD_MARGIN", "Margins are fractions: pct 0-0.30, deltaPct 0-0.20, floorPct 0-0.30");
        }
        return s with
        {
            Mode = o.Mode ?? s.Mode,
            Pct = o.Pct ?? s.Pct,
            DeltaPct = o.DeltaPct ?? s.DeltaPct,
            Method = o.Method ?? s.Method,
            RemoveMethod = o.RemoveMethod ?? s.RemoveMethod,
            MinOdds = o.MinOdds ?? s.MinOdds,
            MaxOdds = o.MaxOdds ?? s.MaxOdds,
            Ladder = o.Ladder ?? s.Ladder,
            FloorPct = o.FloorPct ?? s.FloorPct,
        };
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            throw BoProblem.Invalid("REASON_REQUIRED", "Give a reason (up to 500 characters)");
        }
    }

    private static DateTime? Expiry(int? ttlMinutes, DateTime? expiresAt, bool required)
    {
        if (ttlMinutes is { } ttl)
        {
            return ttl is >= 1 and <= 10080
                ? DateTime.UtcNow.AddMinutes(ttl)
                : throw BoProblem.Invalid("BAD_TTL", "ttlMinutes must be 1-10080");
        }
        if (expiresAt is { } at)
        {
            var utc = at.ToUniversalTime();
            return utc > DateTime.UtcNow ? utc : throw BoProblem.Invalid("BAD_TTL", "expiresAt must be in the future");
        }
        return required ? throw BoProblem.Invalid("TTL_REQUIRED", "Overrides need an expiry (ttlMinutes or expiresAt)") : null;
    }

    private static void ValidateOutcomes(IReadOnlyList<ManualOutcome> outcomes, HashSet<string> allowed, bool creating)
    {
        if (outcomes.Select(o => o.Code).Distinct().Count() != outcomes.Count)
        {
            throw BoProblem.Invalid("DUPLICATE_OUTCOME", "An outcome appears twice");
        }
        foreach (var o in outcomes)
        {
            if (!allowed.Contains(o.Code))
            {
                throw BoProblem.Invalid("UNKNOWN_OUTCOME", $"Outcome '{o.Code}' is not defined for this market type (allowed: {string.Join(", ", allowed.Order())})");
            }
            if (o.Odds is < 1.01m or > 10000m)
            {
                throw BoProblem.Invalid("BAD_ODDS", $"{o.Code}: odds must be between 1.01 and 10000");
            }
            if (creating && o.Odds is null && o.IsActive == true)
            {
                throw BoProblem.Invalid("BAD_ODDS", $"{o.Code}: an active outcome needs odds");
            }
        }
    }

    private sealed record Specifiers(string Normalized, JsonObject Json);

    /// <summary>The template's specifiers, all of them, with values of the declared type (total=2.5, hcp=-1).</summary>
    private static async Task<Specifiers> SpecifiersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int typeId, string? raw)
    {
        var defs = (await conn.QueryAsync<(string Name, string Type)>(
            "SELECT name, type::text FROM sb.market_specifier_def WHERE market_description_id = @typeId", new { typeId }, tx)).ToDictionary(d => d.Name, d => d.Type);
        var normalized = UofFeedParser.NormalizeSpecifiers(raw);
        var pairs = normalized.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToList();
        if (pairs.Any(p => p.Length != 2 || p[1].Length == 0) || pairs.Select(p => p[0]).Distinct().Count() != pairs.Count)
        {
            throw BoProblem.Invalid("BAD_SPECIFIERS", "Specifiers look like 'total=2.5' or 'hcp=-1|total=2.5'");
        }
        var given = pairs.ToDictionary(p => p[0], p => p[1]);
        if (!given.Keys.ToHashSet().SetEquals(defs.Keys))
        {
            throw BoProblem.Invalid("BAD_SPECIFIERS", defs.Count == 0
                ? "This market type takes no specifiers"
                : $"This market type needs: {string.Join(", ", defs.Keys.Order())}");
        }
        foreach (var (name, value) in given)
        {
            var ok = defs[name] switch
            {
                "integer" => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
                "decimal" => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
                _ => value.Length <= 100,
            };
            if (!ok)
            {
                throw BoProblem.Invalid("BAD_SPECIFIERS", $"{name} must be a {defs[name]} value");
            }
        }
        var json = new JsonObject();
        foreach (var (name, value) in given)
        {
            json[name] = value;
        }
        return new Specifiers(normalized, json);
    }

    /// <summary>
    /// The operator's manual copy of a feed market type (<c>manual:{operator}:{code}</c>) with its specifiers and outcomes,
    /// created on first use. Per operator because sb.market is unique on (event, market type, specifiers): two operators
    /// may add the same special to one event. Names keep coming from the origin (<c>attributes.manual_of</c>).
    /// </summary>
    private static async Task<int> ManualTypeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long operatorId, int templateId, string templateCode)
    {
        var code = $"manual:{operatorId}:{templateCode}";
        var inserted = await conn.ExecuteScalarAsync<int?>("""
            INSERT INTO sb.market_description (code, name_template_i18n, groups, outcome_kind, is_variant, attributes)
            SELECT @code, d.name_template_i18n, d.groups, d.outcome_kind, false, d.attributes || jsonb_build_object('manual_of', d.id)
            FROM sb.market_description d WHERE d.id = @templateId
            ON CONFLICT (code) DO NOTHING RETURNING id
            """, new { code, templateId }, tx);
        if (inserted is not { } id)
        {
            return await conn.ExecuteScalarAsync<int>("SELECT id FROM sb.market_description WHERE code = @code", new { code }, tx);
        }
        await conn.ExecuteAsync("""
            INSERT INTO sb.market_specifier_def (market_description_id, name, type, description, ordinal)
            SELECT @id, name, type, description, ordinal FROM sb.market_specifier_def WHERE market_description_id = @templateId;
            INSERT INTO sb.market_description_outcome (market_description_id, variant, code, name_template_i18n, ordinal)
            SELECT @id, variant, code, name_template_i18n, ordinal FROM sb.market_description_outcome
            WHERE market_description_id = @templateId AND variant = '';
            """, new { id, templateId }, tx);
        return id;
    }

    private sealed record MarketRef(long EventId, string Status, bool IsManual, string EventStatus);

    /// <summary>A market the operator may see (feed, or a manual market of theirs); 404 otherwise.</summary>
    private static async Task<MarketRef> MarketAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long marketId) =>
        await conn.QuerySingleOrDefaultAsync<MarketRef>($"""
            SELECT m.event_id AS eventId, m.status::text AS status, coalesce(m.source_producer_id = 0, false) AS isManual, e.status::text AS eventStatus
            FROM sb.market m JOIN sb.event e ON e.id = m.event_id WHERE m.id = @marketId AND {OfferService.VisibleMarketSql}
            """, new { marketId }, tx) ?? throw BoProblem.NotFound("Market");

    private static async Task<Dictionary<long, string>> EventNamesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Names names, long operatorId, string lang, long[] eventIds)
    {
        if (eventIds.Length == 0)
        {
            return [];
        }
        var rows = (await conn.QueryAsync<(long EventId, long CompetitorId, string NameI18n)>("""
            SELECT ec.event_id, c.id, c.name_i18n::text FROM sb.event_competitor ec JOIN sb.competitor c ON c.id = ec.competitor_id
            WHERE ec.event_id = ANY(@eventIds) ORDER BY ec.event_id, ec.position
            """, new { eventIds }, tx)).ToList();
        var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang);
        var resolved = await names.ResolveAsync(conn, tx, "competitor",
            rows.DistinctBy(r => r.CompetitorId).ToDictionary(r => r.CompetitorId.ToString(), r => (string?)r.NameI18n), langs);
        return eventIds.ToDictionary(id => id, id =>
            rows.Where(r => r.EventId == id).Select(r => resolved[r.CompetitorId.ToString()].Text).ToList() is { Count: > 0 } c
                ? string.Join(" v ", c)
                : $"#{id}");
    }

    private sealed class OverrideListRow
    {
        public long Id { get; init; }
        public long MarketId { get; init; }
        public long EventId { get; init; }
        public string OutcomeCode { get; init; } = "";
        public string Kind { get; init; } = "";
        public decimal Value { get; init; }
        public string ClearOn { get; init; } = "";
        public decimal? FeedOddsAtSet { get; init; }
        public decimal? FeedOdds { get; init; }
        public DateTime ExpiresAt { get; init; }
        public string Reason { get; init; } = "";
        public string? CreatedByName { get; init; }
        public DateTime CreatedAt { get; init; }
        public string MarketTypeCode { get; init; } = "";
        public string Specifiers { get; init; } = "";
    }

    private sealed class TradingListRow
    {
        public long Id { get; init; }
        public long? OperatorId { get; init; }
        public string ScopeType { get; init; } = "";
        public long ScopeId { get; init; }
        public string Action { get; init; } = "";
        public DateTime? ExpiresAt { get; init; }
        public string Reason { get; init; } = "";
        public string? CreatedByName { get; init; }
        public DateTime CreatedAt { get; init; }
        public long EventId { get; init; }
        public string? MarketTypeCode { get; init; }
        public string? Specifiers { get; init; }
    }
}

/// <summary>
/// Ends odds overrides whose TTL passed, whose market was settled or cancelled, or (clear_on=feed_change) whose feed
/// price moved past the tolerance, and suspensions whose expiry passed (docs/06 §4.3, §7.3). Offer.Core already ignores
/// expired overrides at read time; this keeps the tables honest and emits the outbox events. Runs outside the tenant
/// role, like the outbox relay.
/// </summary>
public sealed class TradingExpiryWorker(NpgsqlDataSource db, ILogger<TradingExpiryWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(db, stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Trading expiry sweep failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public static async Task<int> RunOnceAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>("""
            WITH odds AS (
              UPDATE bo.odds_override o SET cleared_at = now(),
                cleared_by_name = CASE WHEN m.status IN ('settled', 'cancelled') THEN 'system:settled'
                                       WHEN o.expires_at <= now() THEN 'system:expiry' ELSE 'system:feed_change' END
              FROM sb.market m
              WHERE m.id = o.market_id AND o.cleared_at IS NULL
                AND (o.expires_at <= now() OR m.status IN ('settled', 'cancelled')
                     OR (o.clear_on = 'feed_change' AND o.feed_odds_at_set > 0 AND EXISTS (
                           SELECT 1 FROM sb.outcome oc WHERE oc.market_id = o.market_id AND oc.code = o.outcome_code AND oc.odds IS NOT NULL
                             AND abs(oc.odds - o.feed_odds_at_set) / o.feed_odds_at_set > coalesce(
                               (SELECT (s.value #>> '{}')::numeric FROM bo.setting s
                                WHERE s.operator_id = o.operator_id AND s.scope_type = 'operator' AND s.key = 'odds.override_feed_tolerance_pct'),
                               0.10))))
              RETURNING o.id, o.operator_id, o.market_id, m.event_id, o.cleared_by_name),
            trading AS (
              UPDATE bo.trading_override SET cleared_at = now(), cleared_by_name = 'system:expiry'
              WHERE cleared_at IS NULL AND expires_at <= now()
              RETURNING id, operator_id, scope_type, scope_id),
            audit AS (
              INSERT INTO bo.audit_log (operator_id, actor_type, actor_name, action, entity_type, entity_id, after)
              SELECT operator_id, 'system', cleared_by_name, 'odds.override.cleared', 'odds_override', id::text, jsonb_build_object('marketId', market_id) FROM odds
              UNION ALL
              SELECT operator_id, 'system', 'system:expiry', 'odds.trading.cleared', scope_type, scope_id::text, jsonb_build_object('id', id) FROM trading),
            outbox AS (
              INSERT INTO bo.outbox (operator_id, topic, payload)
              SELECT DISTINCT operator_id, 'bo.changed.' || operator_id || '.odds', jsonb_build_object('eventId', event_id, 'marketId', market_id) FROM odds
              UNION ALL
              SELECT operator_id, 'bo.changed.' || coalesce(operator_id::text, 'platform') || '.trading',
                     jsonb_build_object('scopeType', scope_type, 'scopeId', scope_id, 'action', 'expired') FROM trading)
            SELECT (SELECT count(*) FROM odds)::int + (SELECT count(*) FROM trading)::int
            """);
    }
}
