using System.Text.Json;
using Bo.Core.Cms;
using Bo.Core.Config;
using Bo.Core.Security;
using Dapper;
using Npgsql;
using Platform.Canonical.Db;

namespace Bo.Api.Infrastructure;

/// <summary>Schema migrations and syncing the code catalogs (permissions, system roles, settings, messages) into the DB.</summary>
public static class CatalogSync
{
    public static async Task RunAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await MigrationRunner.MigrateAsync(db, ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // One writer at a time when several instances start together.
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(0x424F4341)", transaction: tx);

        foreach (var p in Permissions.All)
        {
            await conn.ExecuteAsync("""
                INSERT INTO bo.permission (code, module_code, description, risk_level, platform_only)
                VALUES (@Code, @Module, @Description, @risk, @PlatformOnly)
                ON CONFLICT (code) DO UPDATE SET module_code = EXCLUDED.module_code, description = EXCLUDED.description,
                  risk_level = EXCLUDED.risk_level, platform_only = EXCLUDED.platform_only
                """, new { p.Code, p.Module, p.Description, risk = p.Risk.ToString().ToLowerInvariant(), p.PlatformOnly }, tx);
        }

        foreach (var role in Permissions.SystemRoles)
        {
            var id = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO bo.role (operator_id, code, name, is_system, is_platform) VALUES (NULL, @Code, @Name, true, @IsPlatform)
                ON CONFLICT (operator_id, code) DO UPDATE SET name = EXCLUDED.name, is_platform = EXCLUDED.is_platform
                RETURNING id
                """, role, tx);
            await conn.ExecuteAsync("DELETE FROM bo.role_permission WHERE role_id = @id AND permission_code <> ALL(@codes)",
                new { id, codes = role.Permissions.ToArray() }, tx);
            await conn.ExecuteAsync("""
                INSERT INTO bo.role_permission (role_id, permission_code) SELECT @id, unnest(@codes) ON CONFLICT DO NOTHING
                """, new { id, codes = role.Permissions.Distinct().ToArray() }, tx);
        }

        foreach (var d in SettingCatalog.All)
        {
            await conn.ExecuteAsync("""
                INSERT INTO bo.setting_def (key, module, value_type, enum_values, min_value, max_value, allowed_scopes, allows_market_type,
                  default_value, combine, customer_combine, is_operator_editable, requires_approval, description)
                VALUES (@Key, @Module, @type, @EnumValues, @Min, @Max, @scopes, @AllowsMarketType, @def::jsonb, @combine, @CustomerCombine,
                  @OperatorEditable, @RequiresApproval, @Description)
                ON CONFLICT (key) DO UPDATE SET module = EXCLUDED.module, value_type = EXCLUDED.value_type, enum_values = EXCLUDED.enum_values,
                  min_value = EXCLUDED.min_value, max_value = EXCLUDED.max_value, allowed_scopes = EXCLUDED.allowed_scopes,
                  allows_market_type = EXCLUDED.allows_market_type, default_value = EXCLUDED.default_value, combine = EXCLUDED.combine,
                  customer_combine = EXCLUDED.customer_combine, is_operator_editable = EXCLUDED.is_operator_editable,
                  requires_approval = EXCLUDED.requires_approval, description = EXCLUDED.description
                """, new
            {
                d.Key,
                d.Module,
                type = ToSnake(d.Type.ToString()),
                EnumValues = d.EnumValues?.ToArray(),
                d.Min,
                d.Max,
                scopes = d.AllowedScopes.Select(s => s.ToDb()).ToArray(),
                d.AllowsMarketType,
                def = d.Default?.ToJsonString(),
                combine = ToSnake(d.Combine.ToString()),
                d.CustomerCombine,
                d.OperatorEditable,
                d.RequiresApproval,
                d.Description,
            }, tx);
        }

        foreach (var m in MessageCatalog.All)
        {
            await conn.ExecuteAsync("""
                INSERT INTO bo.message_def (code, module, category, severity, params, defaults, is_customer_visible, description)
                VALUES (@Code, @Module, @Category, @Severity, @ps, @defaults::jsonb, @CustomerVisible, @Description)
                ON CONFLICT (code) DO UPDATE SET module = EXCLUDED.module, category = EXCLUDED.category, severity = EXCLUDED.severity,
                  params = EXCLUDED.params, defaults = EXCLUDED.defaults, is_customer_visible = EXCLUDED.is_customer_visible,
                  description = EXCLUDED.description
                """, new
            {
                m.Code, m.Module, m.Category, m.Severity, ps = m.Params.ToArray(),
                defaults = JsonSerializer.Serialize(new Dictionary<string, MessageText> { ["en"] = m.En, ["ka"] = m.Ka }, JsonWeb),
                m.CustomerVisible, m.Description,
            }, tx);
        }
        await conn.ExecuteAsync("DELETE FROM bo.message_def WHERE code <> ALL(@codes)", new { codes = MessageCatalog.All.Select(m => m.Code).ToArray() }, tx);
        await tx.CommitAsync(ct);
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private static string ToSnake(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

/// <summary>
/// Development data: two operators with brands and users whose ids match the Keycloak realm import
/// (deploy/keycloak/bo-realm.json). Idempotent; enabled with <c>Bo:DevSeed=true</c>.
/// </summary>
public static class DevSeed
{
    public static readonly Guid PlatformAdmin = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid PlatformSupport = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid AcmeAdmin = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid AcmeHeadTrader = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid AcmeTrader = Guid.Parse("20000000-0000-0000-0000-000000000003");
    public static readonly Guid BetgeoAdmin = Guid.Parse("30000000-0000-0000-0000-000000000001");

    public static async Task RunAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var acme = await OperatorAsync(conn, tx, "acmebet", "AcmeBet", ["GEL", "USD"]);
        var betgeo = await OperatorAsync(conn, tx, "betgeo", "BetGeo", ["GEL"]);
        await BrandAsync(conn, tx, acme, "acmebet-ge", "AcmeBet Georgia", "local", "acmebet.ge");
        await BrandAsync(conn, tx, acme, "acmebet-com", "AcmeBet International", "foreign", "acmebet.com");
        await BrandAsync(conn, tx, betgeo, "betgeo", "BetGeo", "all", "betgeo.ge");

        await UserAsync(conn, tx, PlatformAdmin, null, "platform", "Platform Admin", "platform_superadmin");
        await UserAsync(conn, tx, PlatformSupport, null, "support", "Platform Support", "platform_support");
        await UserAsync(conn, tx, AcmeAdmin, acme, "acme-admin", "Acme Admin", "operator_admin");
        await UserAsync(conn, tx, AcmeHeadTrader, acme, "acme-head", "Acme Head Trader", "head_trader");
        await UserAsync(conn, tx, AcmeTrader, acme, "acme-trader", "Acme Trader", "trader");
        await UserAsync(conn, tx, BetgeoAdmin, betgeo, "betgeo-admin", "BetGeo Admin", "operator_admin");
        await tx.CommitAsync(ct);
    }

    private static async Task<long> OperatorAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string code, string name, string[] currencies)
    {
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO bo.operator (code, name, status, currencies) VALUES (@code, @name, 'active', @currencies::char(3)[])
            ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name RETURNING id
            """, new { code, name, currencies }, tx);
        await conn.ExecuteAsync("INSERT INTO bo.config_version (operator_id, version) VALUES (@id, 0) ON CONFLICT (operator_id) DO NOTHING", new { id }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO bo.operator_module (operator_id, module_code, enabled) SELECT @id, unnest(@modules), true ON CONFLICT DO NOTHING
            """, new { id, modules = Modules.Platform.PlatformEndpoints.ModuleCodes }, tx);
        return id;
    }

    private static Task BrandAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long operatorId, string code, string name, string audience, string domain) =>
        conn.ExecuteAsync("""
            INSERT INTO bo.brand (operator_id, code, name, audience, primary_domain) VALUES (@operatorId, @code, @name, @audience, @domain)
            ON CONFLICT (operator_id, code) DO NOTHING
            """, new { operatorId, code, name, audience, domain }, tx);

    private static async Task UserAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, long? operatorId, string username, string name, string role)
    {
        await conn.ExecuteAsync("""
            INSERT INTO bo.admin_user (id, operator_id, username, email, display_name, status)
            VALUES (@id, @operatorId, @username, @email, @name, 'active') ON CONFLICT (id) DO NOTHING
            """, new { id, operatorId, username, email = $"{username}@example.test", name }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO bo.user_role (user_id, role_id) SELECT @id, r.id FROM bo.role r WHERE r.operator_id IS NULL AND r.code = @role
            ON CONFLICT DO NOTHING
            """, new { id, role }, tx);
    }
}

/// <summary>
/// Publishes outbox rows. Until NATS is deployed (docs/04) events go to PostgreSQL NOTIFY <c>bo_events</c>;
/// the relay is the only reader of the table and runs outside the tenant role.
/// </summary>
public sealed class OutboxRelay(NpgsqlDataSource db, ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(stoppingToken);
                await using var tx = await conn.BeginTransactionAsync(stoppingToken);
                var rows = (await conn.QueryAsync<(long Id, string Topic, string Payload)>("""
                    SELECT id, topic, payload::text FROM bo.outbox WHERE published_at IS NULL ORDER BY id LIMIT 200 FOR UPDATE SKIP LOCKED
                    """, transaction: tx)).ToList();
                foreach (var row in rows)
                {
                    await conn.ExecuteAsync("SELECT pg_notify('bo_events', @msg)",
                        new { msg = JsonSerializer.Serialize(new { row.Id, row.Topic, Payload = JsonDocument.Parse(row.Payload).RootElement }) }, tx);
                }
                if (rows.Count > 0)
                {
                    await conn.ExecuteAsync("UPDATE bo.outbox SET published_at = now() WHERE id = ANY(@ids)", new { ids = rows.Select(r => r.Id).ToArray() }, tx);
                }
                await tx.CommitAsync(stoppingToken);
                if (rows.Count < 200)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Outbox relay failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
