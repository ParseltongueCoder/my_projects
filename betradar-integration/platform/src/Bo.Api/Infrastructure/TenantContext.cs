using Bo.Core.Security;
using Dapper;
using Npgsql;

namespace Bo.Api.Infrastructure;

public sealed record OperatorRef(long Id, string Code, string Name);

/// <summary>
/// Who is calling and for which operator (docs/08 §1.4-1.5). Resolved once per request from the token and
/// <c>bo.admin_user</c>; the operator of an operator user comes from their membership, never from the request.
/// Platform staff pick an operator with the <c>X-Operator-Id</c> header and act read-only unless they hold
/// <c>platform.impersonate_write</c>.
/// </summary>
public sealed class TenantContext
{
    public Guid UserId { get; private set; }
    public string Username { get; private set; } = "";
    public string? DisplayName { get; private set; }
    public bool IsPlatform { get; private set; }
    public OperatorRef? Operator { get; private set; }
    public IReadOnlySet<string> Permissions { get; private set; } = new HashSet<string>();
    public bool IsResolved { get; private set; }

    public long? OperatorId => Operator?.Id;
    public bool Impersonating => IsPlatform && Operator is not null;
    public string ActorType => IsPlatform ? "platform_user" : "bo_user";
    public string ActorName => DisplayName ?? Username;

    public bool Has(string permission)
    {
        if (!Permissions.Contains(permission))
        {
            return false;
        }
        // Platform staff acting as an operator stay read-only on operator data.
        return !(Impersonating && Core.Security.Permissions.IsWrite(permission)
                 && !Permissions.Contains(Core.Security.Permissions.PlatformImpersonateWrite));
    }

    public void Require(string permission)
    {
        if (!Has(permission))
        {
            throw BoProblem.Forbidden("PERMISSION_DENIED", $"Missing permission '{permission}'");
        }
    }

    /// <summary>Endpoints over operator data need an operator in context (platform staff: X-Operator-Id).</summary>
    public long RequireOperator() =>
        OperatorId ?? throw BoProblem.Invalid("OPERATOR_REQUIRED", "Select an operator (X-Operator-Id) for this request");

    public void RequirePlatform()
    {
        if (!IsPlatform)
        {
            throw BoProblem.Forbidden("PLATFORM_ONLY", "Only platform staff can do this");
        }
    }

    internal void Set(Guid userId, string username, string? displayName, bool isPlatform, OperatorRef? op, IReadOnlySet<string> permissions)
    {
        UserId = userId;
        Username = username;
        DisplayName = displayName;
        IsPlatform = isPlatform;
        Operator = op;
        Permissions = permissions;
        IsResolved = true;
    }

    /// <summary>For background work and seeding.</summary>
    public static TenantContext System(OperatorRef? op = null)
    {
        var t = new TenantContext();
        t.Set(Guid.Empty, "system", "system", true, op, Core.Security.Permissions.All.Select(p => p.Code).ToHashSet());
        return t;
    }
}

/// <summary>Resolves <see cref="TenantContext"/> for /api/bo requests (system connection: before tenant is known).</summary>
internal sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, TenantContext tenant, NpgsqlDataSource db)
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated == true && Guid.TryParse(user.FindFirst(BoClaims.Subject)?.Value, out var userId))
        {
            await ResolveAsync(http, tenant, db, userId);
        }
        await next(http);
    }

    private static async Task ResolveAsync(HttpContext http, TenantContext tenant, NpgsqlDataSource db, Guid userId)
    {
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>("""
            SELECT u.id, u.username, u.display_name AS displayname, u.status, u.operator_id AS operatorid,
                   u.allowed_operators AS allowedoperators, o.code AS operatorcode, o.name AS operatorname, o.status AS operatorstatus
            FROM bo.admin_user u LEFT JOIN bo.operator o ON o.id = u.operator_id
            WHERE u.id = @userId
            """, new { userId });
        if (row is null)
        {
            throw BoProblem.Forbidden("USER_NOT_PROVISIONED", "This account has no back-office access");
        }
        if (row.Status == "disabled")
        {
            throw BoProblem.Forbidden("USER_DISABLED", "This account is disabled");
        }

        OperatorRef? op;
        var isPlatform = row.OperatorId is null;
        if (!isPlatform)
        {
            // The token's organization must be the user's operator: a user moved between orgs in Keycloak
            // but not here (or the reverse) gets no access rather than the wrong tenant.
            if (!BoClaims.Organizations(http.User).Contains(row.OperatorCode!, StringComparer.Ordinal))
            {
                throw BoProblem.Forbidden("TENANT_MISMATCH", "Token organization does not match the account's operator");
            }
            if (row.OperatorStatus is "suspended" or "terminated")
            {
                throw BoProblem.Forbidden("OPERATOR_SUSPENDED", "The operator is not active");
            }
            op = new OperatorRef(row.OperatorId!.Value, row.OperatorCode!, row.OperatorName!);
        }
        else
        {
            if (!BoClaims.IsPlatformStaff(http.User))
            {
                throw BoProblem.Forbidden("PLATFORM_ROLE_MISSING", "Platform account without the platform-staff role");
            }
            op = null;
            if (http.Request.Headers["X-Operator-Id"].FirstOrDefault() is { Length: > 0 } header)
            {
                if (!long.TryParse(header, out var requested))
                {
                    throw BoProblem.Invalid("BAD_OPERATOR_ID", "X-Operator-Id must be a number");
                }
                if (row.AllowedOperators is { } allowed && !allowed.Contains(requested))
                {
                    throw BoProblem.Forbidden("OPERATOR_NOT_ALLOWED", "Not allowed to act for this operator");
                }
                op = await conn.QuerySingleOrDefaultAsync<OperatorRef>(
                    "SELECT id, code, name FROM bo.operator WHERE id = @requested", new { requested })
                    ?? throw BoProblem.NotFound("Operator");
            }
        }

        var permissions = (await conn.QueryAsync<string>("""
            SELECT DISTINCT rp.permission_code
            FROM bo.user_role ur JOIN bo.role_permission rp ON rp.role_id = ur.role_id
            WHERE ur.user_id = @userId AND (ur.expires_at IS NULL OR ur.expires_at > now())
            """, new { userId })).ToHashSet(StringComparer.Ordinal);

        if (isPlatform && op is not null && !permissions.Contains(Permissions.PlatformImpersonateRead))
        {
            throw BoProblem.Forbidden("PERMISSION_DENIED", $"Missing permission '{Permissions.PlatformImpersonateRead}'");
        }

        await conn.ExecuteAsync("""
            UPDATE bo.admin_user SET last_login_at = now(), status = CASE WHEN status = 'invited' THEN 'active' ELSE status END
            WHERE id = @userId AND (last_login_at IS NULL OR last_login_at < now() - interval '5 minutes')
            """, new { userId });

        tenant.Set(userId, row.Username, row.DisplayName, isPlatform, op, permissions);
    }

    private sealed class UserRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string Status { get; init; } = "";
        public long? OperatorId { get; init; }
        public long[]? AllowedOperators { get; init; }
        public string? OperatorCode { get; init; }
        public string? OperatorName { get; init; }
        public string? OperatorStatus { get; init; }
    }
}
