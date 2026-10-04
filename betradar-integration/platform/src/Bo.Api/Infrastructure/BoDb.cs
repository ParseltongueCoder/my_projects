using System.Text.Json;
using Dapper;
using Npgsql;

namespace Bo.Api.Infrastructure;

/// <summary>
/// Runs work in one transaction under the tenant: <c>SET LOCAL ROLE bo_app</c> (row-level security applies) plus
/// <c>app.operator_id</c> / <c>app.platform</c> for the policies (V005). Both are transaction-local, so pooled
/// connections never leak a tenant. In production the login role must be a member of <c>bo_app</c>.
/// </summary>
public sealed class BoDb(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource => dataSource;

    public async Task<T> TenantAsync<T>(TenantContext tenant, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("""
            SET LOCAL ROLE bo_app;
            SELECT set_config('app.operator_id', @op, true), set_config('app.platform', @platform, true);
            """, new { op = tenant.OperatorId?.ToString() ?? "", platform = tenant.IsPlatform ? "on" : "" }, tx);
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task TenantAsync(TenantContext tenant, Func<NpgsqlConnection, NpgsqlTransaction, Task> work, CancellationToken ct = default) =>
        TenantAsync<bool>(tenant, async (c, t) =>
        {
            await work(c, t);
            return true;
        }, ct);

    /// <summary>Switches the tenant inside a running transaction (platform staff creating an operator's first rows).</summary>
    public static Task SwitchOperatorAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long operatorId) =>
        conn.ExecuteAsync("SELECT set_config('app.operator_id', @op, true)", new { op = operatorId.ToString() }, tx);
}

/// <summary>Append-only audit log and transactional outbox, written in the caller's transaction (docs/08 §1.1).</summary>
public static class Audit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TenantContext actor, string action, string entityType, string entityId,
        object? before = null, object? after = null, string? reason = null, long? operatorId = null, string? requestId = null) =>
        conn.ExecuteAsync("""
            INSERT INTO bo.audit_log (operator_id, actor_id, actor_type, actor_name, action, entity_type, entity_id, before, after, reason, request_id)
            VALUES (@operatorId, @actorId, @actorType, @actorName, @action, @entityType, @entityId,
                    @before::jsonb, @after::jsonb, @reason, @requestId)
            """, new
        {
            operatorId = operatorId ?? actor.OperatorId,
            actorId = actor.UserId == Guid.Empty ? (Guid?)null : actor.UserId,
            actorType = actor.UserId == Guid.Empty ? "system" : actor.ActorType,
            actorName = actor.ActorName,
            action,
            entityType,
            entityId,
            before = before is null ? null : JsonSerializer.Serialize(before, Json),
            after = after is null ? null : JsonSerializer.Serialize(after, Json),
            reason,
            requestId,
        }, tx);

    public static Task OutboxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long? operatorId, string topic, object payload) =>
        conn.ExecuteAsync("INSERT INTO bo.outbox (operator_id, topic, payload) VALUES (@operatorId, @topic, @payload::jsonb)",
            new { operatorId, topic, payload = JsonSerializer.Serialize(payload, Json) }, tx);
}
