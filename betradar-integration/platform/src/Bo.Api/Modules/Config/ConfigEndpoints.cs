using Bo.Api.Infrastructure;
using Bo.Core.Config;
using Bo.Core.Security;
using Dapper;

namespace Bo.Api.Modules.Config;

public static class ConfigEndpoints
{
    public static RouteGroupBuilder MapConfig(this RouteGroupBuilder api)
    {
        var cfg = api.MapGroup("/cfg");

        cfg.MapGet("/defs", (TenantContext t) =>
        {
            t.Require(Permissions.CfgView);
            return SettingCatalog.All.Select(d => new
            {
                d.Key,
                d.Module,
                Type = d.Type.ToString().ToLowerInvariant(),
                AllowedScopes = d.AllowedScopes.Select(s => s.ToDb()),
                d.AllowsMarketType,
                d.Default,
                Combine = d.Combine.ToString(),
                d.EnumValues,
                d.Min,
                d.Max,
                d.OperatorEditable,
                d.RequiresApproval,
                d.CustomerCombine,
                d.Description,
            });
        });

        cfg.MapGet("/scope", (TenantContext t, ConfigService svc, string scopeType, long? scopeId, int? marketTypeId, CancellationToken ct) =>
            ScopeTypes.TryParse(scopeType, out var scope)
                ? svc.ScopeAsync(t, scope, scopeId, marketTypeId, ct)
                : throw BoProblem.Invalid("BAD_SCOPE", $"Unknown scope type '{scopeType}'"));

        cfg.MapGet("/effective", (TenantContext t, ConfigService svc, string? keys, long? brandId, long? sportId, long? categoryId,
                long? tournamentId, long? eventId, long? marketId, int? marketTypeId, CancellationToken ct) =>
            svc.EffectiveAsync(t, new ScopeQuery(brandId, sportId, categoryId, tournamentId, eventId, marketId, marketTypeId),
                keys?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ct));

        cfg.MapGet("/change-sets", (TenantContext t, ConfigService svc, string? status, int? limit, CancellationToken ct) =>
            svc.ListAsync(t, status, null, Math.Clamp(limit ?? 50, 1, 200), ct));
        cfg.MapGet("/change-sets/{id:long}", (TenantContext t, ConfigService svc, long id, CancellationToken ct) => svc.GetAsync(t, id, ct));
        cfg.MapPost("/change-sets", async (TenantContext t, ConfigService svc, ChangeSetRequest body, CancellationToken ct) =>
        {
            var set = await svc.CreateAsync(t, body, ct);
            return Results.Created($"/api/bo/cfg/change-sets/{set.Id}", set);
        });
        cfg.MapPost("/change-sets/{id:long}/approve", (TenantContext t, ConfigService svc, long id, DecisionRequest? body, CancellationToken ct) =>
            svc.DecideAsync(t, id, true, body?.Comment, ct));
        cfg.MapPost("/change-sets/{id:long}/reject", (TenantContext t, ConfigService svc, long id, DecisionRequest? body, CancellationToken ct) =>
            svc.DecideAsync(t, id, false, body?.Comment, ct));

        // Scope pickers: the feed catalogue (read-only here; CAT will add overrides and manual entities).
        cfg.MapGet("/scopes", (TenantContext t, BoDb db, string? q, string? type, CancellationToken ct) =>
        {
            t.Require(Permissions.CfgView);
            var term = $"%{(q ?? "").Trim()}%";
            return db.TenantAsync(t, async (conn, tx) => (await conn.QueryAsync<ScopeOption>("""
                SELECT * FROM (
                  SELECT 'sport' AS type, s.id::bigint AS id, s.name_i18n->>'en' AS name, NULL::text AS path FROM sb.sport s
                  UNION ALL
                  SELECT 'category', c.id, c.name_i18n->>'en', s.name_i18n->>'en'
                  FROM sb.category c JOIN sb.sport s ON s.id = c.sport_id
                  UNION ALL
                  SELECT 'tournament', t.id, t.name_i18n->>'en', concat_ws(' / ', s.name_i18n->>'en', c.name_i18n->>'en')
                  FROM sb.tournament t JOIN sb.category c ON c.id = t.category_id JOIN sb.sport s ON s.id = t.sport_id
                  UNION ALL
                  SELECT 'event', e.id,
                         coalesce(e.name_i18n->>'en',
                                  (SELECT string_agg(co.name_i18n->>'en', ' v ' ORDER BY ec.position)
                                   FROM sb.event_competitor ec JOIN sb.competitor co ON co.id = ec.competitor_id WHERE ec.event_id = e.id)),
                         concat_ws(' / ', s.name_i18n->>'en', t.name_i18n->>'en')
                  FROM sb.event e JOIN sb.sport s ON s.id = e.sport_id LEFT JOIN sb.tournament t ON t.id = e.tournament_id
                ) x
                WHERE (@type::text IS NULL OR type = @type) AND (name ILIKE @term OR path ILIKE @term)
                ORDER BY array_position(ARRAY['sport','category','tournament','event'], type), name
                LIMIT 30
                """, new { term, type }, tx)).ToList(), ct);
        });

        cfg.MapGet("/market-types", (TenantContext t, BoDb db, string? q, CancellationToken ct) =>
        {
            t.Require(Permissions.CfgView);
            var term = $"%{(q ?? "").Trim()}%";
            return db.TenantAsync(t, async (conn, tx) => (await conn.QueryAsync<MarketTypeOption>("""
                SELECT id, coalesce(name_template_i18n->>'en', code) AS name FROM sb.market_description
                WHERE name_template_i18n->>'en' ILIKE @term OR code ILIKE @term OR id::text = @raw ORDER BY id LIMIT 30
                """, new { term, raw = (q ?? "").Trim() }, tx)).ToList(), ct);
        });

        return api;
    }

    public sealed record DecisionRequest(string? Comment);

    public sealed record ScopeOption(string Type, long Id, string? Name, string? Path);

    public sealed record MarketTypeOption(int Id, string Name);
}
