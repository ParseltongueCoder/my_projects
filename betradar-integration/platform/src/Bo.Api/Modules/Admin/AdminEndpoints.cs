using System.Text;
using System.Text.Json.Nodes;
using Bo.Api.Identity;
using Bo.Api.Infrastructure;
using Bo.Core.Security;
using Dapper;
using Npgsql;

namespace Bo.Api.Modules.Admin;

/// <summary>ADM (docs/08 §3): the current user, users and role grants, roles and permissions, audit log.</summary>
public static class AdminEndpoints
{
    public sealed record UserDto(
        Guid Id, long? OperatorId, string Username, string Email, string? DisplayName, string Status,
        DateTime? LastLoginAt, DateTime CreatedAt, int Version, IReadOnlyList<string> Roles);

    public sealed record CreateUserRequest(string Username, string Email, string? DisplayName, IReadOnlyList<string> Roles);

    public sealed record SetRolesRequest(IReadOnlyList<string> Roles, string Reason);

    public sealed record ReasonRequest(string Reason);

    public sealed record RoleDto(long Id, long? OperatorId, string Code, string Name, bool IsSystem, bool IsPlatform, IReadOnlyList<string> Permissions);

    public sealed record AuditDto(
        long Id, DateTime Ts, long? OperatorId, Guid? ActorId, string ActorType, string? ActorName, string Action,
        string EntityType, string EntityId, JsonNode? Before, JsonNode? After, string? Reason);

    public static RouteGroupBuilder MapAdmin(this RouteGroupBuilder api)
    {
        api.MapGet("/me", (TenantContext t, BoDb db, CancellationToken ct) => db.TenantAsync(t, async (conn, tx) =>
        {
            // Platform staff: operators they may act for. Operator users: their own operator only (RLS).
            var operators = (await conn.QueryAsync<OperatorRef>("SELECT id, code, name FROM bo.operator ORDER BY name", transaction: tx))
                .Where(o => t.IsPlatform || o.Id == t.OperatorId).ToList();
            var allowed = t.IsPlatform
                ? await conn.ExecuteScalarAsync<long[]?>("SELECT allowed_operators FROM bo.admin_user WHERE id = @id", new { id = t.UserId }, tx)
                : null;
            return new
            {
                user = new { id = t.UserId, username = t.Username, displayName = t.DisplayName },
                isPlatform = t.IsPlatform,
                @operator = t.Operator,
                impersonating = t.Impersonating,
                readOnly = t.Impersonating && !t.Permissions.Contains(Permissions.PlatformImpersonateWrite),
                permissions = t.Permissions.Where(t.Has).Order(StringComparer.Ordinal),
                operators = operators.Where(o => allowed is null || allowed.Contains(o.Id)),
            };
        }, ct));

        var adm = api.MapGroup("/adm");

        adm.MapGet("/permissions", (TenantContext t) =>
        {
            t.Require(Permissions.AdmUserView);
            return Permissions.All.Where(p => t.IsPlatform || !p.PlatformOnly)
                .Select(p => new { p.Code, p.Module, p.Description, Risk = p.Risk.ToString().ToLowerInvariant(), p.PlatformOnly });
        });

        adm.MapGet("/roles", (TenantContext t, BoDb db, CancellationToken ct) =>
        {
            t.Require(Permissions.AdmUserView);
            return db.TenantAsync(t, (conn, tx) => RolesAsync(conn, tx, platformRoles: t.IsPlatform && t.OperatorId is null));
        });

        adm.MapGet("/users", (TenantContext t, BoDb db, CancellationToken ct) =>
        {
            t.Require(Permissions.AdmUserView);
            return db.TenantAsync(t, (conn, tx) => UsersAsync(conn, tx, null), ct);
        });

        adm.MapPost("/users", async (TenantContext t, BoDb db, IIdentityProvisioner idp, CreateUserRequest body, CancellationToken ct) =>
        {
            t.Require(Permissions.AdmUserEdit);
            var platformUser = t.IsPlatform && t.OperatorId is null;
            var username = body.Username.Trim().ToLowerInvariant();
            if (username.Length < 3 || !body.Email.Contains('@'))
            {
                throw BoProblem.Invalid("INVALID_USER", "Username (3+ characters) and a valid email are required");
            }
            var roles = await db.TenantAsync(t, (conn, tx) => ResolveRolesAsync(conn, tx, body.Roles, platformUser), ct);
            var provisioned = await idp.CreateUserAsync(username, body.Email.Trim(), body.DisplayName, t.Operator?.Code, platformUser, ct);
            var user = await db.TenantAsync(t, async (conn, tx) =>
            {
                await conn.ExecuteAsync("""
                    INSERT INTO bo.admin_user (id, operator_id, username, email, display_name, status)
                    VALUES (@id, @operatorId, @username, @email, @displayName, 'invited')
                    """, new { id = provisioned.Id, operatorId = t.OperatorId, username, email = body.Email.Trim(), displayName = body.DisplayName }, tx);
                foreach (var role in roles)
                {
                    await conn.ExecuteAsync("INSERT INTO bo.user_role (user_id, role_id, granted_by) VALUES (@user, @role, @by)",
                        new { user = provisioned.Id, role = role.Id, by = t.UserId }, tx);
                }
                var created = (await UsersAsync(conn, tx, provisioned.Id)).Single();
                await Audit.WriteAsync(conn, tx, t, "adm.user.created", "admin_user", provisioned.Id.ToString(), after: created);
                return created;
            }, ct);
            return Results.Created($"/api/bo/adm/users/{user.Id}", new { user, temporaryPassword = provisioned.TemporaryPassword });
        });

        adm.MapPut("/users/{id:guid}/roles", (TenantContext t, BoDb db, Guid id, SetRolesRequest body, CancellationToken ct) =>
        {
            t.Require(Permissions.AdmUserEdit);
            RequireReason(body.Reason);
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var before = (await UsersAsync(conn, tx, id)).SingleOrDefault() ?? throw BoProblem.NotFound("User");
                var roles = await ResolveRolesAsync(conn, tx, body.Roles, before.OperatorId is null);
                if (before.Roles.Contains("operator_admin") && !roles.Any(r => r.Code == "operator_admin"))
                {
                    await EnsureNotLastAdminAsync(conn, tx, id);
                }
                await conn.ExecuteAsync("DELETE FROM bo.user_role WHERE user_id = @id", new { id }, tx);
                foreach (var role in roles)
                {
                    await conn.ExecuteAsync("INSERT INTO bo.user_role (user_id, role_id, granted_by) VALUES (@id, @role, @by)",
                        new { id, role = role.Id, by = t.UserId }, tx);
                }
                await conn.ExecuteAsync("UPDATE bo.admin_user SET version = version + 1 WHERE id = @id", new { id }, tx);
                var after = (await UsersAsync(conn, tx, id)).Single();
                await Audit.WriteAsync(conn, tx, t, "adm.user.roles_changed", "admin_user", id.ToString(),
                    new { before.Roles }, new { after.Roles }, body.Reason);
                return after;
            }, ct);
        });

        adm.MapPost("/users/{id:guid}/disable", (TenantContext t, BoDb db, IIdentityProvisioner idp, Guid id, ReasonRequest body, CancellationToken ct) =>
            SetStatusAsync(t, db, idp, id, enabled: false, body.Reason, ct));
        adm.MapPost("/users/{id:guid}/enable", (TenantContext t, BoDb db, IIdentityProvisioner idp, Guid id, ReasonRequest body, CancellationToken ct) =>
            SetStatusAsync(t, db, idp, id, enabled: true, body.Reason, ct));

        adm.MapGet("/audit", (TenantContext t, BoDb db, string? entityType, string? entityId, string? action, Guid? actorId,
            DateTime? from, DateTime? to, string? cursor, int? limit, CancellationToken ct) =>
        {
            t.Require(Permissions.AdmAuditView);
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var (beforeTs, beforeId) = DecodeCursor(cursor);
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var rows = (await conn.QueryAsync<AuditRow>("""
                    SELECT id, ts, operator_id AS operatorid, actor_id AS actorid, actor_type AS actortype, actor_name AS actorname, action,
                           entity_type AS entitytype, entity_id AS entityid, before::text AS before, after::text AS after, reason
                    FROM bo.audit_log
                    WHERE (@entityType::text IS NULL OR entity_type = @entityType)
                      AND (@entityId::text IS NULL OR entity_id = @entityId)
                      AND (@action::text IS NULL OR action LIKE @action || '%')
                      AND (@actorId::uuid IS NULL OR actor_id = @actorId)
                      AND (@from::timestamptz IS NULL OR ts >= @from)
                      AND (@to::timestamptz IS NULL OR ts < @to)
                      AND (@beforeTs::timestamptz IS NULL OR (ts, id) < (@beforeTs, @beforeId))
                    ORDER BY ts DESC, id DESC
                    LIMIT @take
                    """, new { entityType, entityId, action, actorId, from, to, beforeTs, beforeId, take = take + 1 }, tx)).ToList();
                var page = rows.Take(take).Select(r => new AuditDto(r.Id, r.Ts, r.OperatorId, r.ActorId, r.ActorType, r.ActorName, r.Action,
                    r.EntityType, r.EntityId, r.Before is null ? null : JsonNode.Parse(r.Before), r.After is null ? null : JsonNode.Parse(r.After), r.Reason)).ToList();
                return new { items = page, nextCursor = rows.Count > take ? EncodeCursor(page[^1].Ts, page[^1].Id) : null };
            }, ct);
        });

        return api;
    }

    private static async Task<UserDto> SetStatusAsync(TenantContext t, BoDb db, IIdentityProvisioner idp, Guid id, bool enabled, string reason, CancellationToken ct)
    {
        t.Require(Permissions.AdmUserEdit);
        RequireReason(reason);
        if (id == t.UserId)
        {
            throw BoProblem.Conflict("SELF_DISABLE", "You cannot disable your own account");
        }
        var user = await db.TenantAsync(t, async (conn, tx) =>
        {
            var before = (await UsersAsync(conn, tx, id)).SingleOrDefault() ?? throw BoProblem.NotFound("User");
            if (!enabled && before.Roles.Contains("operator_admin"))
            {
                await EnsureNotLastAdminAsync(conn, tx, id);
            }
            await conn.ExecuteAsync("UPDATE bo.admin_user SET status = @status, version = version + 1 WHERE id = @id",
                new { id, status = enabled ? "active" : "disabled" }, tx);
            var after = (await UsersAsync(conn, tx, id)).Single();
            await Audit.WriteAsync(conn, tx, t, enabled ? "adm.user.enabled" : "adm.user.disabled", "admin_user", id.ToString(),
                new { before.Status }, new { after.Status }, reason);
            return after;
        }, ct);
        // Keycloak after the DB: a disabled DB row already blocks every API call, even if Keycloak lags.
        await idp.SetEnabledAsync(id, enabled, ct);
        return user;
    }

    private static async Task EnsureNotLastAdminAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId)
    {
        var others = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM bo.admin_user u
            JOIN bo.user_role ur ON ur.user_id = u.id JOIN bo.role r ON r.id = ur.role_id
            WHERE r.code = 'operator_admin' AND u.status <> 'disabled' AND u.id <> @userId
              AND u.operator_id IS NOT DISTINCT FROM (SELECT operator_id FROM bo.admin_user WHERE id = @userId)
            """, new { userId }, tx);
        if (others == 0)
        {
            throw BoProblem.Conflict("LAST_ADMIN", "The operator's last active admin cannot lose that role");
        }
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            throw BoProblem.Invalid("REASON_REQUIRED", "A reason (5+ characters) is required");
        }
    }

    private static async Task<IReadOnlyList<RoleDto>> ResolveRolesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<string> codes, bool platformUser)
    {
        var all = await RolesAsync(conn, tx, platformUser);
        var picked = new List<RoleDto>();
        foreach (var code in codes.Distinct())
        {
            picked.Add(all.FirstOrDefault(r => r.Code == code)
                ?? throw BoProblem.Invalid("UNKNOWN_ROLE", $"Role '{code}' cannot be granted here"));
        }
        return picked;
    }

    private static async Task<IReadOnlyList<RoleDto>> RolesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, bool platformRoles)
    {
        var roles = await conn.QueryAsync<RoleRow>("""
            SELECT r.id, r.operator_id AS operatorid, r.code, r.name, r.is_system AS issystem, r.is_platform AS isplatform,
                   coalesce(array_agg(rp.permission_code ORDER BY rp.permission_code) FILTER (WHERE rp.permission_code IS NOT NULL), '{}') AS permissions
            FROM bo.role r LEFT JOIN bo.role_permission rp ON rp.role_id = r.id
            WHERE r.is_platform = @platformRoles
            GROUP BY r.id ORDER BY r.is_system DESC, r.id
            """, new { platformRoles }, tx);
        return roles.Select(r => new RoleDto(r.Id, r.OperatorId, r.Code, r.Name, r.IsSystem, r.IsPlatform, r.Permissions)).ToList();
    }

    private static async Task<IReadOnlyList<UserDto>> UsersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid? id)
    {
        // RLS: operator context → that operator's users; platform context without operator → platform staff.
        var users = await conn.QueryAsync<UserRow>("""
            SELECT u.id, u.operator_id AS operatorid, u.username, u.email, u.display_name AS displayname, u.status,
                   u.last_login_at AS lastloginat, u.created_at AS createdat, u.version,
                   coalesce(array_agg(r.code ORDER BY r.code) FILTER (WHERE r.code IS NOT NULL), '{}') AS roles
            FROM bo.admin_user u
            LEFT JOIN bo.user_role ur ON ur.user_id = u.id AND (ur.expires_at IS NULL OR ur.expires_at > now())
            LEFT JOIN bo.role r ON r.id = ur.role_id
            WHERE (@id::uuid IS NULL OR u.id = @id)
              AND (u.operator_id = bo.current_operator() OR (bo.current_operator() IS NULL AND u.operator_id IS NULL))
            GROUP BY u.id ORDER BY u.username
            """, new { id }, tx);
        return users.Select(u => new UserDto(u.Id, u.OperatorId, u.Username, u.Email, u.DisplayName, u.Status, u.LastLoginAt, u.CreatedAt, u.Version, u.Roles)).ToList();
    }

    private static string EncodeCursor(DateTime ts, long id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ts.Ticks}:{id}"));

    private static (DateTime? Ts, long Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, 0);
        }
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            return (new DateTime(long.Parse(parts[0]), DateTimeKind.Utc), long.Parse(parts[1]));
        }
        catch (FormatException)
        {
            throw BoProblem.Invalid("BAD_CURSOR", "Invalid cursor");
        }
    }

    private sealed class UserRow
    {
        public Guid Id { get; init; }
        public long? OperatorId { get; init; }
        public string Username { get; init; } = "";
        public string Email { get; init; } = "";
        public string? DisplayName { get; init; }
        public string Status { get; init; } = "";
        public DateTime? LastLoginAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public int Version { get; init; }
        public string[] Roles { get; init; } = [];
    }

    private sealed class RoleRow
    {
        public long Id { get; init; }
        public long? OperatorId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsSystem { get; init; }
        public bool IsPlatform { get; init; }
        public string[] Permissions { get; init; } = [];
    }

    private sealed class AuditRow
    {
        public long Id { get; init; }
        public DateTime Ts { get; init; }
        public long? OperatorId { get; init; }
        public Guid? ActorId { get; init; }
        public string ActorType { get; init; } = "";
        public string? ActorName { get; init; }
        public string Action { get; init; } = "";
        public string EntityType { get; init; } = "";
        public string EntityId { get; init; } = "";
        public string? Before { get; init; }
        public string? After { get; init; }
        public string? Reason { get; init; }
    }
}
