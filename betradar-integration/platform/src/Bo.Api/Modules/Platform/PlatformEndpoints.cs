using System.Text.RegularExpressions;
using Bo.Api.Identity;
using Bo.Api.Infrastructure;
using Bo.Core.Security;
using Dapper;

namespace Bo.Api.Modules.Platform;

/// <summary>Operators (tenants), their brands (sites) and enabled modules (docs/08 §1.4, docs/09 §2.1).</summary>
public static partial class PlatformEndpoints
{
    public static readonly string[] ModuleCodes =
        ["CAT", "I18N", "ODDS", "CFG", "CMS", "BET", "LIM", "CASH", "CUS", "MON", "REP", "ADM", "PROMO", "NOTIF", "INT", "WL"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex Code();

    public sealed class OperatorDto
    {
        public long Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string Timezone { get; init; } = "";
        public string BaseCurrency { get; init; } = "";
        public string[] Currencies { get; init; } = [];
        public string[] Languages { get; init; } = [];
        public string Jurisdiction { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public int Version { get; init; }
    }

    public sealed record CreateOperatorRequest(string Code, string Name, string? Timezone, string? BaseCurrency, string[]? Currencies, string[]? Languages);

    public sealed record UpdateOperatorRequest(string? Name, string? Status, string Reason);

    public sealed record BrandDto(long Id, long OperatorId, string Code, string Name, string Audience, string? PrimaryDomain, string Status, int Version);

    public sealed record CreateBrandRequest(string Code, string Name, string? Audience, string? PrimaryDomain);

    public sealed record ModulesRequest(Dictionary<string, bool> Modules, string Reason);

    private const string OperatorColumns = """
        id, code, name, status, timezone, base_currency AS basecurrency, currencies::text[] AS currencies, languages,
        jurisdiction, created_at AS createdat, version
        """;

    public static RouteGroupBuilder MapPlatform(this RouteGroupBuilder api)
    {
        var platform = api.MapGroup("/platform");

        platform.MapGet("/operators", (TenantContext t, BoDb db, CancellationToken ct) =>
        {
            t.RequirePlatform();
            return db.TenantAsync(t, async (conn, tx) =>
                (await conn.QueryAsync<OperatorDto>($"SELECT {OperatorColumns} FROM bo.operator ORDER BY name", transaction: tx)).ToList(), ct);
        });

        platform.MapPost("/operators", async (TenantContext t, BoDb db, IIdentityProvisioner idp, CreateOperatorRequest body, CancellationToken ct) =>
        {
            t.RequirePlatform();
            t.Require(Permissions.PlatformOperatorManage);
            var code = body.Code.Trim().ToLowerInvariant();
            if (!Code().IsMatch(code) || string.IsNullOrWhiteSpace(body.Name))
            {
                throw BoProblem.Invalid("INVALID_OPERATOR", "Code: 2-40 lowercase letters, digits or '-'; name required");
            }
            var baseCurrency = (body.BaseCurrency ?? "GEL").ToUpperInvariant();
            var currencies = (body.Currencies is { Length: > 0 } c ? c : [baseCurrency]).Select(x => x.ToUpperInvariant()).Distinct().ToArray();
            if (!currencies.Contains(baseCurrency))
            {
                throw BoProblem.Invalid("INVALID_OPERATOR", "The base currency must be one of the currencies");
            }
            // Keycloak first: the organization alias is the tenant key in tokens.
            await idp.CreateOrganizationAsync(code, body.Name.Trim(), ct);
            var created = await db.TenantAsync(t, async (conn, tx) =>
            {
                if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM bo.operator WHERE code = @code)", new { code }, tx))
                {
                    throw BoProblem.Conflict("OPERATOR_EXISTS", $"Operator '{code}' already exists");
                }
                var id = await conn.ExecuteScalarAsync<long>("""
                    INSERT INTO bo.operator (code, name, timezone, base_currency, currencies, languages)
                    VALUES (@code, @name, @tz, @baseCurrency, @currencies::char(3)[], @languages) RETURNING id
                    """, new { code, name = body.Name.Trim(), tz = body.Timezone ?? "Asia/Tbilisi", baseCurrency, currencies, languages = body.Languages ?? ["ka", "en", "ru"] }, tx);
                await BoDb.SwitchOperatorAsync(conn, tx, id);
                await conn.ExecuteAsync("INSERT INTO bo.config_version (operator_id, version) VALUES (@id, 0)", new { id }, tx);
                foreach (var module in ModuleCodes)
                {
                    await conn.ExecuteAsync("INSERT INTO bo.operator_module (operator_id, module_code, enabled) VALUES (@id, @module, true)", new { id, module }, tx);
                }
                await conn.ExecuteAsync("INSERT INTO bo.brand (operator_id, code, name) VALUES (@id, @code, @name)", new { id, code, name = body.Name.Trim() }, tx);
                var op = await conn.QuerySingleAsync<OperatorDto>($"SELECT {OperatorColumns} FROM bo.operator WHERE id = @id", new { id }, tx);
                await Audit.WriteAsync(conn, tx, t, "platform.operator.created", "operator", id.ToString(), after: op, operatorId: id);
                return op;
            }, ct);
            return Results.Created($"/api/bo/platform/operators/{created.Id}", created);
        });

        platform.MapPatch("/operators/{id:long}", (TenantContext t, BoDb db, long id, UpdateOperatorRequest body, CancellationToken ct) =>
        {
            t.RequirePlatform();
            t.Require(Permissions.PlatformOperatorManage);
            if (string.IsNullOrWhiteSpace(body.Reason))
            {
                throw BoProblem.Invalid("REASON_REQUIRED", "A reason is required");
            }
            if (body.Status is not (null or "onboarding" or "active" or "suspended" or "terminated"))
            {
                throw BoProblem.Invalid("BAD_STATUS", "Unknown operator status");
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var before = await conn.QuerySingleOrDefaultAsync<OperatorDto>($"SELECT {OperatorColumns} FROM bo.operator WHERE id = @id", new { id }, tx)
                             ?? throw BoProblem.NotFound("Operator");
                var after = await conn.QuerySingleAsync<OperatorDto>($"""
                    UPDATE bo.operator SET name = coalesce(@name, name), status = coalesce(@status, status), version = version + 1
                    WHERE id = @id RETURNING {OperatorColumns}
                    """, new { id, name = body.Name, status = body.Status }, tx);
                await Audit.WriteAsync(conn, tx, t, "platform.operator.updated", "operator", id.ToString(), before, after, body.Reason, operatorId: id);
                return after;
            }, ct);
        });

        // Brands and modules of the operator in context (operator users: their own; platform staff: X-Operator-Id).
        api.MapGet("/brands", (TenantContext t, BoDb db, CancellationToken ct) =>
        {
            t.Require(Permissions.BrandView);
            t.RequireOperator();
            return db.TenantAsync(t, async (conn, tx) => (await conn.QueryAsync<BrandDto>("""
                SELECT id, operator_id AS operatorid, code, name, audience, primary_domain AS primarydomain, status, version
                FROM bo.brand ORDER BY id
                """, transaction: tx)).ToList(), ct);
        });

        api.MapPost("/brands", (TenantContext t, BoDb db, CreateBrandRequest body, CancellationToken ct) =>
        {
            t.Require(Permissions.PlatformOperatorManage);
            var operatorId = t.RequireOperator();
            var code = body.Code.Trim().ToLowerInvariant();
            if (!Code().IsMatch(code) || string.IsNullOrWhiteSpace(body.Name) || body.Audience is not (null or "all" or "local" or "foreign"))
            {
                throw BoProblem.Invalid("INVALID_BRAND", "Code: 2-40 lowercase letters, digits or '-'; name required; audience all|local|foreign");
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var brand = await conn.QuerySingleAsync<BrandDto>("""
                    INSERT INTO bo.brand (operator_id, code, name, audience, primary_domain)
                    VALUES (@operatorId, @code, @name, @audience, @domain)
                    RETURNING id, operator_id AS operatorid, code, name, audience, primary_domain AS primarydomain, status, version
                    """, new { operatorId, code, name = body.Name.Trim(), audience = body.Audience ?? "all", domain = body.PrimaryDomain }, tx);
                await Audit.WriteAsync(conn, tx, t, "platform.brand.created", "brand", brand.Id.ToString(), after: brand);
                return Results.Created($"/api/bo/brands/{brand.Id}", brand);
            }, ct);
        });

        api.MapGet("/modules", (TenantContext t, BoDb db, CancellationToken ct) =>
        {
            t.RequireOperator();
            return db.TenantAsync(t, async (conn, tx) =>
                (await conn.QueryAsync<(string Module, bool Enabled)>("SELECT module_code, enabled FROM bo.operator_module", transaction: tx))
                .ToDictionary(m => m.Module, m => m.Enabled), ct);
        });

        api.MapPut("/modules", (TenantContext t, BoDb db, ModulesRequest body, CancellationToken ct) =>
        {
            t.Require(Permissions.PlatformOperatorManage);
            var operatorId = t.RequireOperator();
            if (body.Modules.Keys.Except(ModuleCodes).FirstOrDefault() is { } unknown)
            {
                throw BoProblem.Invalid("UNKNOWN_MODULE", $"Unknown module '{unknown}'");
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                foreach (var (module, enabled) in body.Modules)
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO bo.operator_module (operator_id, module_code, enabled) VALUES (@operatorId, @module, @enabled)
                        ON CONFLICT (operator_id, module_code) DO UPDATE SET enabled = EXCLUDED.enabled
                        """, new { operatorId, module, enabled }, tx);
                }
                await Audit.WriteAsync(conn, tx, t, "platform.modules.changed", "operator", operatorId.ToString(), after: body.Modules, reason: body.Reason);
                return Results.NoContent();
            }, ct);
        });

        return api;
    }
}
