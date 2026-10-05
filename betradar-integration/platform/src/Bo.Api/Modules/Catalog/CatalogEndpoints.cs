using Bo.Api.Infrastructure;
using Bo.Api.Modules.Config;
using Bo.Core.Config;
using Dapper;
using Npgsql;

namespace Bo.Api.Modules.Catalog;

/// <summary>
/// CAT (docs/06 §2): the feed catalogue as one operator sees it — names, order, top leagues, visibility (CFG
/// <c>offer.visible</c>), images; events with display overrides; participants. Feed rows (<c>sb</c>) are never changed.
/// </summary>
public static class CatalogEndpoints
{
    public sealed record MediaRef(string Role, Guid MediaId, string Url, bool Inherited);

    public sealed record TreeNode(
        string Type, long Id, string FeedName, string Name, string NameSource, bool Visible, bool HiddenHere,
        int? SortOrder, bool IsTop, int? TopOrder, string? Slug, int OpenEvents, string? CountryCode,
        IReadOnlyList<MediaRef> Media, IReadOnlyList<TreeNode> Children);

    public sealed record NodePatch(int? SortOrder, bool? IsTop, int? TopOrder, string? Slug, bool ClearSlug = false);

    public sealed record OrderRequest(string Type, IReadOnlyList<long> Ids);

    public sealed record VisibilityRequest(IReadOnlyList<NodeRef> Targets, bool Visible, string Reason);

    public sealed record NodeRef(string Type, long Id);

    public sealed record EventRow(
        long Id, string? Urn, string Name, string? TournamentName, long? TournamentId, long SportId, string SportName,
        DateTime? ScheduledAt, DateTime? DisplayStartAt, string Status, bool IsFeatured, int OpenMarkets);

    public sealed record EventOverrideRequest(DateTime? DisplayStartAt, bool IsFeatured, int? FeaturedOrder,
        DateTime? FeaturedFrom, DateTime? FeaturedTo, string? Note, int? Version);

    private static readonly string[] NodeTypes = ["sport", "category", "tournament"];

    public static RouteGroupBuilder MapCatalog(this RouteGroupBuilder api)
    {
        var cat = api.MapGroup("/cat");

        cat.MapGet("/tree", (TenantContext t, BoDb db, Names names, SettingsSnapshotCache settings, string? lang, CancellationToken ct) =>
        {
            t.Require("cat.view");
            var operatorId = t.RequireOperator();
            return db.TenantAsync(t, (conn, tx) => TreeAsync(conn, tx, names, settings, operatorId, lang ?? "ka"), ct);
        });

        cat.MapPatch("/nodes/{type}/{id:long}", (TenantContext t, BoDb db, string type, long id, NodePatch body, CancellationToken ct) =>
        {
            t.Require("cat.edit");
            var operatorId = t.RequireOperator();
            RequireNodeType(type);
            return db.TenantAsync(t, async (conn, tx) =>
            {
                await EnsureNodeAsync(conn, tx, type, id);
                var before = await NodeRowAsync(conn, tx, type, id);
                await conn.ExecuteAsync("""
                    INSERT INTO bo.catalog_node (operator_id, node_type, node_id, sort_order, is_top, top_order, slug, updated_by)
                    VALUES (@operatorId, @type, @id, @sortOrder, coalesce(@isTop, false), @topOrder, @slug, @user)
                    ON CONFLICT (operator_id, node_type, node_id) DO UPDATE SET
                      sort_order = coalesce(@sortOrder, bo.catalog_node.sort_order),
                      is_top = coalesce(@isTop, bo.catalog_node.is_top),
                      top_order = coalesce(@topOrder, bo.catalog_node.top_order),
                      slug = CASE WHEN @clearSlug THEN NULL ELSE coalesce(@slug, bo.catalog_node.slug) END,
                      updated_by = @user, updated_at = now(), version = bo.catalog_node.version + 1
                    """, new { operatorId, type, id, body.SortOrder, body.IsTop, body.TopOrder, slug = body.Slug, clearSlug = body.ClearSlug, user = t.UserId }, tx);
                var after = await NodeRowAsync(conn, tx, type, id);
                await Audit.WriteAsync(conn, tx, t, "cat.node.updated", type, id.ToString(), before, after);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId}.cat", new { type, id });
                return after;
            }, ct);
        });

        // Drag & drop: the given ids get sort order 0..n-1 (siblings not listed keep theirs).
        cat.MapPut("/order", (TenantContext t, BoDb db, OrderRequest body, CancellationToken ct) =>
        {
            t.Require("cat.edit");
            var operatorId = t.RequireOperator();
            RequireNodeType(body.Type);
            return db.TenantAsync(t, async (conn, tx) =>
            {
                for (var i = 0; i < body.Ids.Count; i++)
                {
                    await EnsureNodeAsync(conn, tx, body.Type, body.Ids[i]);
                    await conn.ExecuteAsync("""
                        INSERT INTO bo.catalog_node (operator_id, node_type, node_id, sort_order, updated_by)
                        VALUES (@operatorId, @type, @id, @i, @user)
                        ON CONFLICT (operator_id, node_type, node_id) DO UPDATE SET sort_order = @i, updated_by = @user,
                          updated_at = now(), version = bo.catalog_node.version + 1
                        """, new { operatorId, type = body.Type, id = body.Ids[i], i, user = t.UserId }, tx);
                }
                await Audit.WriteAsync(conn, tx, t, "cat.order.changed", body.Type, string.Join(',', body.Ids), after: body.Ids);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId}.cat", new { body.Type, body.Ids });
                return Results.NoContent();
            }, ct);
        });

        // Visibility is the inheritable CFG policy offer.visible: hiding writes false at the node, showing removes it.
        cat.MapPost("/visibility", (TenantContext t, ConfigService cfg, VisibilityRequest body, CancellationToken ct) =>
        {
            t.Require("cat.edit");
            if (body.Targets.Count == 0)
            {
                throw BoProblem.Invalid("TARGETS_REQUIRED", "Choose at least one node");
            }
            foreach (var target in body.Targets)
            {
                if (target.Type is not ("sport" or "category" or "tournament" or "event"))
                {
                    throw BoProblem.Invalid("BAD_NODE", $"Unknown node type '{target.Type}'");
                }
            }
            var changes = body.Targets.Select(x => new SettingChangeRequest(
                body.Visible ? "delete" : "upsert", x.Type, x.Id, null, "offer.visible", body.Visible ? null : System.Text.Json.Nodes.JsonValue.Create(false))).ToList();
            var title = $"{(body.Visible ? "Show" : "Hide")} {string.Join(", ", body.Targets.Select(x => $"{x.Type} #{x.Id}"))}: {body.Reason}";
            return cfg.CreateAsync(t, new ChangeSetRequest(title, false, changes), ct);
        });

        cat.MapGet("/events", (TenantContext t, BoDb db, Names names, string? q, long? sportId, long? tournamentId, string? status,
            DateTime? from, DateTime? to, bool? featured, string? lang, int? page, int? pageSize, CancellationToken ct) =>
        {
            t.Require("cat.view");
            var operatorId = t.RequireOperator();
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var offset = Math.Max(0, (page ?? 1) - 1) * size;
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var rows = (await conn.QueryAsync<EventSql>($"""
                    SELECT e.id, pm.provider_entity_id AS urn, e.name_i18n::text AS nameI18n, e.tournament_id::bigint AS tournamentId,
                           t.name_i18n::text AS tournamentI18n, e.sport_id::bigint AS sportId, s.name_i18n::text AS sportI18n,
                           e.scheduled_at AS scheduledAt, o.display_start_at AS displayStartAt, e.status::text AS status,
                           coalesce(o.is_featured, false) AS isFeatured,
                           (SELECT count(*)::int FROM sb.market m WHERE m.event_id = e.id AND m.status IN ('active','suspended') AND {Odds.OfferService.VisibleMarketSql}) AS openMarkets,
                           count(*) OVER ()::int AS total
                    FROM sb.event e
                    JOIN sb.sport s ON s.id = e.sport_id
                    LEFT JOIN sb.tournament t ON t.id = e.tournament_id
                    LEFT JOIN bo.event_override o ON o.event_id = e.id
                    LEFT JOIN LATERAL (SELECT provider_entity_id FROM sb.provider_mapping
                                       WHERE entity_type = 'event' AND internal_id = e.id ORDER BY provider_id DESC LIMIT 1) pm ON true
                    WHERE (@sportId::bigint IS NULL OR e.sport_id = @sportId)
                      AND (@tournamentId::bigint IS NULL OR e.tournament_id = @tournamentId)
                      AND (@status::text IS NULL OR e.status::text = @status)
                      AND (@from::timestamptz IS NULL OR e.scheduled_at >= @from)
                      AND (@to::timestamptz IS NULL OR e.scheduled_at < @to)
                      AND (@featured::boolean IS NULL OR coalesce(o.is_featured, false) = @featured)
                      AND (@q::text IS NULL OR pm.provider_entity_id = @q OR e.id::text = @q OR EXISTS (
                            SELECT 1 FROM sb.event_competitor ec JOIN sb.competitor c ON c.id = ec.competitor_id
                            WHERE ec.event_id = e.id AND (c.name_i18n::text ILIKE '%' || @q || '%' OR EXISTS (
                              SELECT 1 FROM bo.translation tr WHERE tr.entity_type = 'competitor' AND tr.entity_id = c.id::text
                                AND tr.text ILIKE '%' || @q || '%'))))
                    ORDER BY (e.status = 'live') DESC, e.scheduled_at NULLS LAST, e.id
                    LIMIT @size OFFSET @offset
                    """, new { sportId, tournamentId, status, from, to, featured, q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(), size, offset }, tx)).ToList();
                var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang ?? "ka");
                var items = await NameEventsAsync(conn, tx, names, langs, rows);
                return new { items, total = rows.FirstOrDefault()?.Total ?? 0, page = page ?? 1, pageSize = size };
            }, ct);
        });

        cat.MapGet("/events/{id:long}", (TenantContext t, BoDb db, Names names, SettingsSnapshotCache settings, long id, string? lang, CancellationToken ct) =>
        {
            t.Require("cat.view");
            var operatorId = t.RequireOperator();
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var row = await conn.QuerySingleOrDefaultAsync<EventSql>($"""
                    SELECT e.id, pm.provider_entity_id AS urn, e.name_i18n::text AS nameI18n, e.tournament_id::bigint AS tournamentId,
                           t.name_i18n::text AS tournamentI18n, e.sport_id::bigint AS sportId, s.name_i18n::text AS sportI18n,
                           e.scheduled_at AS scheduledAt, o.display_start_at AS displayStartAt, e.status::text AS status,
                           coalesce(o.is_featured, false) AS isFeatured,
                           (SELECT count(*)::int FROM sb.market m WHERE m.event_id = e.id AND m.status IN ('active','suspended') AND {Odds.OfferService.VisibleMarketSql}) AS openMarkets, 1 AS total
                    FROM sb.event e JOIN sb.sport s ON s.id = e.sport_id LEFT JOIN sb.tournament t ON t.id = e.tournament_id
                    LEFT JOIN bo.event_override o ON o.event_id = e.id
                    LEFT JOIN LATERAL (SELECT provider_entity_id FROM sb.provider_mapping
                                       WHERE entity_type = 'event' AND internal_id = e.id ORDER BY provider_id DESC LIMIT 1) pm ON true
                    WHERE e.id = @id
                    """, new { id }, tx) ?? throw BoProblem.NotFound("Event");
                var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang ?? "ka");
                var item = (await NameEventsAsync(conn, tx, names, langs, [row])).Single();
                var over = await conn.QuerySingleOrDefaultAsync<OverrideRow>("""
                    SELECT display_start_at AS displayStartAt, is_featured AS isFeatured, featured_order AS featuredOrder,
                           featured_from AS featuredFrom, featured_to AS featuredTo, note, version
                    FROM bo.event_override WHERE event_id = @id
                    """, new { id }, tx);
                var competitors = (await conn.QueryAsync<(long Id, int Position, string? Qualifier, string NameI18n)>("""
                    SELECT c.id, ec.position, ec.qualifier::text, c.name_i18n::text FROM sb.event_competitor ec
                    JOIN sb.competitor c ON c.id = ec.competitor_id WHERE ec.event_id = @id ORDER BY ec.position
                    """, new { id }, tx)).ToList();
                var compNames = await names.ResolveAsync(conn, tx, "competitor", competitors.ToDictionary(c => c.Id.ToString(), c => (string?)c.NameI18n), langs);
                var ctx = await ScopePaths.BuildAsync(conn, tx, operatorId, new ScopeQuery(EventId: id));
                var rows = await settings.GetAsync(conn, tx, operatorId);
                var policy = new[] { "offer.visible", "offer.live_enabled", "offer.prematch_enabled" }
                    .ToDictionary(k => k, k => SettingResolver.Resolve(SettingCatalog.ByKey[k], rows, ctx).Value);
                return new
                {
                    @event = item,
                    @override = over,
                    competitors = competitors.Select(c => new { c.Id, c.Position, c.Qualifier, name = compNames[c.Id.ToString()].Text }),
                    policy,
                };
            }, ct);
        });

        cat.MapPut("/events/{id:long}/override", (TenantContext t, BoDb db, long id, EventOverrideRequest body, CancellationToken ct) =>
        {
            t.Require("cat.edit");
            var operatorId = t.RequireOperator();
            if (body.FeaturedFrom is { } f && body.FeaturedTo is { } to && to <= f)
            {
                throw BoProblem.Invalid("BAD_PERIOD", "Featured until must be after featured from");
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM sb.event WHERE id = @id)", new { id }, tx))
                {
                    throw BoProblem.NotFound("Event");
                }
                var before = await conn.QuerySingleOrDefaultAsync<OverrideRow>("""
                    SELECT display_start_at AS displayStartAt, is_featured AS isFeatured, featured_order AS featuredOrder,
                           featured_from AS featuredFrom, featured_to AS featuredTo, note, version
                    FROM bo.event_override WHERE event_id = @id FOR UPDATE
                    """, new { id }, tx);
                if (before is not null && body.Version is { } v && v != before.Version)
                {
                    throw BoProblem.Conflict("VERSION_CONFLICT", "The event was changed by someone else; reload it");
                }
                var after = await conn.QuerySingleAsync<OverrideRow>("""
                    INSERT INTO bo.event_override (operator_id, event_id, display_start_at, is_featured, featured_order, featured_from, featured_to, note, updated_by)
                    VALUES (@operatorId, @id, @DisplayStartAt, @IsFeatured, @FeaturedOrder, @FeaturedFrom, @FeaturedTo, @Note, @user)
                    ON CONFLICT (operator_id, event_id) DO UPDATE SET display_start_at = EXCLUDED.display_start_at,
                      is_featured = EXCLUDED.is_featured, featured_order = EXCLUDED.featured_order, featured_from = EXCLUDED.featured_from,
                      featured_to = EXCLUDED.featured_to, note = EXCLUDED.note, updated_by = EXCLUDED.updated_by, updated_at = now(),
                      version = bo.event_override.version + 1
                    RETURNING display_start_at AS displayStartAt, is_featured AS isFeatured, featured_order AS featuredOrder,
                              featured_from AS featuredFrom, featured_to AS featuredTo, note, version
                    """, new { operatorId, id, body.DisplayStartAt, body.IsFeatured, body.FeaturedOrder, body.FeaturedFrom, body.FeaturedTo, body.Note, user = t.UserId }, tx);
                await Audit.WriteAsync(conn, tx, t, "cat.event.override", "event", id.ToString(), before, after);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId}.cat", new { type = "event", id });
                return after;
            }, ct);
        });

        cat.MapGet("/participants", (TenantContext t, BoDb db, Names names, string? q, long? sportId, string? lang, int? page, CancellationToken ct) =>
        {
            t.Require("cat.view");
            var operatorId = t.RequireOperator();
            var offset = Math.Max(0, (page ?? 1) - 1) * 50;
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var rows = (await conn.QueryAsync<(long Id, string NameI18n, string? Abbreviation, string? CountryCode, long? SportId, int Events, int Total)>("""
                    SELECT c.id, c.name_i18n::text, c.abbreviation, c.country_code::text, c.sport_id::bigint,
                           (SELECT count(*)::int FROM sb.event_competitor ec WHERE ec.competitor_id = c.id), count(*) OVER ()::int
                    FROM sb.competitor c
                    WHERE (@sportId::bigint IS NULL OR c.sport_id = @sportId)
                      AND (@q::text IS NULL OR c.name_i18n::text ILIKE '%' || @q || '%' OR EXISTS (
                           SELECT 1 FROM bo.translation tr WHERE tr.entity_type = 'competitor' AND tr.entity_id = c.id::text AND tr.text ILIKE '%' || @q || '%'))
                    ORDER BY c.name_i18n->>'en' LIMIT 50 OFFSET @offset
                    """, new { sportId, q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(), offset }, tx)).ToList();
                var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang ?? "ka");
                var provider = rows.ToDictionary(r => r.Id.ToString(), r => (string?)r.NameI18n);
                var resolved = await names.ResolveAsync(conn, tx, "competitor", provider, langs);
                var shortNames = await names.ResolveAsync(conn, tx, "competitor", provider.ToDictionary(p => p.Key, _ => (string?)null), langs, "short_name");
                var media = await MediaAsync(conn, tx, "competitor", rows.Select(r => r.Id).ToArray());
                return new
                {
                    items = rows.Select(r => new
                    {
                        r.Id,
                        feedName = Names.Parse(r.NameI18n).GetValueOrDefault("en") ?? r.Id.ToString(),
                        name = resolved[r.Id.ToString()].Text,
                        nameSource = resolved[r.Id.ToString()].Source,
                        shortName = shortNames[r.Id.ToString()].Source == "id" ? null : shortNames[r.Id.ToString()].Text,
                        r.Abbreviation,
                        r.CountryCode,
                        r.SportId,
                        r.Events,
                        media = media[r.Id],
                    }),
                    total = rows.FirstOrDefault().Total,
                };
            }, ct);
        });

        return api;
    }

    private static async Task<IReadOnlyList<TreeNode>> TreeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Names names, SettingsSnapshotCache settings, long operatorId, string lang)
    {
        var sports = (await conn.QueryAsync<NodeSql>("SELECT id::bigint, NULL::bigint AS parentId, name_i18n::text AS nameI18n, NULL::text AS country FROM sb.sport", transaction: tx)).ToList();
        var categories = (await conn.QueryAsync<NodeSql>("SELECT id::bigint, sport_id::bigint AS parentId, name_i18n::text AS nameI18n, country_code::text AS country FROM sb.category", transaction: tx)).ToList();
        var tournaments = (await conn.QueryAsync<NodeSql>("SELECT id::bigint, category_id::bigint AS parentId, name_i18n::text AS nameI18n, NULL::text AS country FROM sb.tournament", transaction: tx)).ToList();
        var openEvents = (await conn.QueryAsync<(long TournamentId, int Count)>("""
            SELECT tournament_id::bigint, count(*)::int FROM sb.event WHERE status IN ('not_started', 'live') AND tournament_id IS NOT NULL GROUP BY 1
            """, transaction: tx)).ToDictionary(x => x.TournamentId, x => x.Count);
        var overrides = (await conn.QueryAsync<(string Type, long Id, int? SortOrder, bool IsTop, int? TopOrder, string? Slug)>(
            "SELECT node_type, node_id, sort_order, is_top, top_order, slug FROM bo.catalog_node", transaction: tx)).ToDictionary(o => (o.Type, o.Id));

        var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang);
        var sportNames = await names.ResolveAsync(conn, tx, "sport", sports.ToDictionary(s => s.Id.ToString(), s => (string?)s.NameI18n), langs);
        var categoryNames = await names.ResolveAsync(conn, tx, "category", categories.ToDictionary(s => s.Id.ToString(), s => (string?)s.NameI18n), langs);
        var tournamentNames = await names.ResolveAsync(conn, tx, "tournament", tournaments.ToDictionary(s => s.Id.ToString(), s => (string?)s.NameI18n), langs);
        var media = new Dictionary<string, ILookup<long, MediaRef>>
        {
            ["sport"] = await MediaAsync(conn, tx, "sport", sports.Select(s => s.Id).ToArray()),
            ["category"] = await MediaAsync(conn, tx, "category", categories.Select(s => s.Id).ToArray()),
            ["tournament"] = await MediaAsync(conn, tx, "tournament", tournaments.Select(s => s.Id).ToArray()),
        };

        var rows = await settings.GetAsync(conn, tx, operatorId);
        var visibleDef = SettingCatalog.ByKey["offer.visible"];

        TreeNode Build(string type, NodeSql n, Dictionary<string, ResolvedName> nameMap, ScopeContext ctx, IReadOnlyList<TreeNode> children, int open)
        {
            var visible = SettingResolver.Resolve(visibleDef, rows, ctx);
            var hiddenHere = rows.Any(r => r.Key == "offer.visible" && r.OperatorId == operatorId && r.ScopeType == ScopeTypes.Parse(type)
                                           && r.ScopeId == n.Id && r.MarketTypeId is null && r.Value.GetValue<bool>() == false);
            overrides.TryGetValue((type, n.Id), out var o);
            var name = nameMap[n.Id.ToString()];
            return new TreeNode(type, n.Id, Names.Parse(n.NameI18n).GetValueOrDefault("en") ?? name.Text, name.Text, name.Source,
                visible.Value!.GetValue<bool>(), hiddenHere, o.SortOrder, o.IsTop, o.TopOrder, o.Slug, open, n.Country,
                media[type][n.Id].ToList(), children);
        }

        IReadOnlyList<TreeNode> Sort(IEnumerable<TreeNode> nodes) =>
            nodes.OrderBy(x => x.SortOrder ?? int.MaxValue).ThenByDescending(x => x.IsTop).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToList();

        return Sort(sports.Select(s =>
        {
            var cats = Sort(categories.Where(c => c.ParentId == s.Id).Select(c =>
            {
                var tours = Sort(tournaments.Where(x => x.ParentId == c.Id).Select(x =>
                    Build("tournament", x, tournamentNames, new ScopeContext(operatorId, SportId: s.Id, CategoryId: c.Id, TournamentId: x.Id), [],
                        openEvents.GetValueOrDefault(x.Id))));
                return Build("category", c, categoryNames, new ScopeContext(operatorId, SportId: s.Id, CategoryId: c.Id), tours, tours.Sum(x => x.OpenEvents));
            }));
            return Build("sport", s, sportNames, new ScopeContext(operatorId, SportId: s.Id), cats, cats.Sum(x => x.OpenEvents));
        }));
    }

    /// <summary>Images per entity: the operator's link wins over the platform default.</summary>
    public static async Task<ILookup<long, MediaRef>> MediaAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string entityType, long[] ids)
    {
        var links = await conn.QueryAsync<(long EntityId, string Role, Guid MediaId, long? OperatorId)>("""
            SELECT entity_id, role, media_id, operator_id FROM bo.media_link WHERE entity_type = @entityType AND entity_id = ANY(@ids)
            """, new { entityType, ids }, tx);
        return links
            .GroupBy(l => (l.EntityId, l.Role))
            .Select(g => g.OrderByDescending(l => l.OperatorId is not null).First())
            .ToLookup(l => l.EntityId, l => new MediaRef(l.Role, l.MediaId, $"/api/media/{l.MediaId}", l.OperatorId is null));
    }

    private static async Task<IReadOnlyList<EventRow>> NameEventsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Names names, IReadOnlyList<string> langs, IReadOnlyList<EventSql> rows)
    {
        var ids = rows.Select(r => r.Id).ToArray();
        var competitors = (await conn.QueryAsync<(long EventId, long CompetitorId, int Position, string NameI18n)>("""
            SELECT ec.event_id, c.id, ec.position, c.name_i18n::text FROM sb.event_competitor ec
            JOIN sb.competitor c ON c.id = ec.competitor_id WHERE ec.event_id = ANY(@ids)
            """, new { ids }, tx)).ToList();
        var compNames = await names.ResolveAsync(conn, tx, "competitor",
            competitors.DistinctBy(c => c.CompetitorId).ToDictionary(c => c.CompetitorId.ToString(), c => (string?)c.NameI18n), langs);
        var eventNames = await names.ResolveAsync(conn, tx, "event", rows.ToDictionary(r => r.Id.ToString(), r => r.NameI18n), langs);
        var tournaments = await names.ResolveAsync(conn, tx, "tournament",
            rows.Where(r => r.TournamentId is not null).DistinctBy(r => r.TournamentId).ToDictionary(r => r.TournamentId!.Value.ToString(), r => r.TournamentI18n), langs);
        var sports = await names.ResolveAsync(conn, tx, "sport", rows.DistinctBy(r => r.SportId).ToDictionary(r => r.SportId.ToString(), r => r.SportI18n), langs);
        var byEvent = competitors.ToLookup(c => c.EventId);
        return rows.Select(r =>
        {
            var comps = byEvent[r.Id].OrderBy(c => c.Position).Select(c => compNames[c.CompetitorId.ToString()].Text).ToList();
            var name = eventNames[r.Id.ToString()] is { Source: not "id" } en ? en.Text : comps.Count > 0 ? string.Join(" v ", comps) : $"#{r.Id}";
            return new EventRow(r.Id, r.Urn, name, r.TournamentId is { } tid ? tournaments[tid.ToString()].Text : null, r.TournamentId,
                r.SportId, sports[r.SportId.ToString()].Text, r.ScheduledAt, r.DisplayStartAt, r.Status, r.IsFeatured, r.OpenMarkets);
        }).ToList();
    }

    private static void RequireNodeType(string type)
    {
        if (!NodeTypes.Contains(type))
        {
            throw BoProblem.Invalid("BAD_NODE", $"Unknown node type '{type}'");
        }
    }

    private static async Task EnsureNodeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, long id)
    {
        if (!await conn.ExecuteScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM sb.{type} WHERE id = @id)", new { id }, tx))
        {
            throw BoProblem.NotFound(type);
        }
    }

    private static Task<object?> NodeRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, long id) =>
        conn.QuerySingleOrDefaultAsync<object?>("""
            SELECT sort_order AS "sortOrder", is_top AS "isTop", top_order AS "topOrder", slug, version
            FROM bo.catalog_node WHERE node_type = @type AND node_id = @id
            """, new { type, id }, tx);

    private sealed class NodeSql
    {
        public long Id { get; init; }
        public long? ParentId { get; init; }
        public string NameI18n { get; init; } = "{}";
        public string? Country { get; init; }
    }

    private sealed class EventSql
    {
        public long Id { get; init; }
        public string? Urn { get; init; }
        public string? NameI18n { get; init; }
        public long? TournamentId { get; init; }
        public string? TournamentI18n { get; init; }
        public long SportId { get; init; }
        public string? SportI18n { get; init; }
        public DateTime? ScheduledAt { get; init; }
        public DateTime? DisplayStartAt { get; init; }
        public string Status { get; init; } = "";
        public bool IsFeatured { get; init; }
        public int OpenMarkets { get; init; }
        public int Total { get; init; }
    }

    public sealed class OverrideRow
    {
        public DateTime? DisplayStartAt { get; init; }
        public bool IsFeatured { get; init; }
        public int? FeaturedOrder { get; init; }
        public DateTime? FeaturedFrom { get; init; }
        public DateTime? FeaturedTo { get; init; }
        public string? Note { get; init; }
        public int Version { get; init; }
    }
}
