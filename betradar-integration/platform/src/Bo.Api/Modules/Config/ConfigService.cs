using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bo.Api.Infrastructure;
using Bo.Core.Config;
using Bo.Core.Security;
using Dapper;
using Npgsql;

namespace Bo.Api.Modules.Config;

public sealed record SettingChangeRequest(string Op, string ScopeType, long? ScopeId, int? MarketTypeId, string Key, JsonNode? Value);

public sealed record ChangeSetRequest(string Title, bool Platform, IReadOnlyList<SettingChangeRequest> Changes);

public sealed record SettingChangeDto(string Op, string ScopeType, long? ScopeId, int? MarketTypeId, string Key, JsonNode? OldValue, JsonNode? NewValue);

public sealed record ChangeSetDto(
    long Id, long? OperatorId, string Title, string Status, Guid CreatedBy, string? CreatedByName, DateTime CreatedAt,
    Guid? DecidedBy, string? DecidedByName, DateTime? DecidedAt, string? DecisionComment, DateTime? AppliedAt, long? ConfigVersion,
    IReadOnlyList<SettingChangeDto> Changes);

/// <summary>
/// CFG engine (docs/06 §5): every change is a change set; keys with <c>requires_approval</c> wait for a second
/// user (four-eyes). Applying a set upserts/deletes rows, records old/new values, bumps the tenant's
/// <c>config_version</c>, audits and emits an outbox event (<c>bo.changed.{operator}.cfg</c>).
/// </summary>
public sealed class ConfigService(BoDb db, SettingsSnapshotCache cache)
{
    /// <summary>Module permission needed on top of cfg.edit to change a module's keys (docs/06 §5.9).</summary>
    private static readonly Dictionary<string, string> ModulePermission = new(StringComparer.Ordinal)
    {
        ["ODDS"] = "odds.margin.edit",
        ["LIM"] = "limit.edit",
        ["CASH"] = "cashout.edit",
        ["MON"] = "referral.decide",
        ["CMS"] = "cms.edit",
        ["I18N"] = "i18n.edit",
        ["CAT"] = "cat.edit",
        ["PROMO"] = "promo.campaign.edit",
    };

    public async Task<ChangeSetDto> CreateAsync(TenantContext t, ChangeSetRequest request, CancellationToken ct)
    {
        t.Require(Permissions.CfgEdit);
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw BoProblem.Invalid("TITLE_REQUIRED", "A change set needs a title (what and why)");
        }
        if (request.Changes.Count is 0 or > 500)
        {
            throw BoProblem.Invalid("CHANGES_REQUIRED", "A change set needs 1-500 changes");
        }
        long? operatorId;
        if (request.Platform)
        {
            t.RequirePlatform();
            t.Require(Permissions.PlatformSettingsEdit);
            operatorId = null;
        }
        else
        {
            operatorId = t.RequireOperator();
        }

        var changes = new List<(SettingChangeRequest Change, SettingDef Def, ScopeType Scope)>();
        foreach (var c in request.Changes)
        {
            if (!SettingCatalog.ByKey.TryGetValue(c.Key, out var def))
            {
                throw BoProblem.Invalid("UNKNOWN_KEY", $"Unknown setting '{c.Key}'");
            }
            if (!ScopeTypes.TryParse(c.ScopeType, out var scope))
            {
                throw BoProblem.Invalid("BAD_SCOPE", $"Unknown scope type '{c.ScopeType}'");
            }
            if ((scope == ScopeType.Platform) != request.Platform)
            {
                throw BoProblem.Invalid("BAD_SCOPE", "A change set targets either platform settings or one operator's settings");
            }
            if (c.Op is not ("upsert" or "delete"))
            {
                throw BoProblem.Invalid("BAD_OP", "op must be 'upsert' or 'delete'");
            }
            if ((scope is ScopeType.Platform or ScopeType.Operator) != (c.ScopeId is null))
            {
                throw BoProblem.Invalid("BAD_SCOPE", $"scopeId is {(c.ScopeId is null ? "required" : "not allowed")} for {scope.ToDb()}");
            }
            if (SettingValidator.Validate(def, scope, c.MarketTypeId, c.Value, t.IsPlatform, c.Op == "delete") is { } error)
            {
                throw BoProblem.Invalid("INVALID_SETTING", $"{c.Key}: {error}");
            }
            if (ModulePermission.TryGetValue(def.Module, out var perm))
            {
                t.Require(perm);
            }
            changes.Add((c, def, scope));
        }
        if (changes.GroupBy(c => (c.Scope, c.Change.ScopeId, c.Change.MarketTypeId, c.Change.Key)).Any(g => g.Count() > 1))
        {
            throw BoProblem.Invalid("DUPLICATE_CHANGE", "The same setting appears twice in the change set");
        }

        var needsApproval = changes.Any(c => c.Def.RequiresApproval);
        var id = await db.TenantAsync(t, async (conn, tx) =>
        {
            foreach (var (c, _, scope) in changes)
            {
                await EnsureScopeExistsAsync(conn, tx, scope, c.ScopeId, c.MarketTypeId);
            }
            var setId = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO bo.setting_change_set (operator_id, title, status, created_by, created_by_name)
                VALUES (@operatorId, @title, @status, @user, @name) RETURNING id
                """, new { operatorId, title = request.Title.Trim(), status = needsApproval ? "pending_approval" : "applied", user = t.UserId, name = t.ActorName }, tx);
            foreach (var (c, _, scope) in changes)
            {
                var current = await CurrentValueAsync(conn, tx, operatorId, scope, c.ScopeId, c.MarketTypeId, c.Key);
                await conn.ExecuteAsync("""
                    INSERT INTO bo.setting_change (change_set_id, op, scope_type, scope_id, market_type_id, key, old_value, new_value)
                    VALUES (@setId, @op, @scope, @scopeId, @mt, @key, @old::jsonb, @new::jsonb)
                    """, new { setId, op = c.Op, scope = scope.ToDb(), scopeId = c.ScopeId, mt = c.MarketTypeId, key = c.Key, old = current, @new = c.Op == "delete" ? null : c.Value?.ToJsonString() }, tx);
            }
            await Audit.WriteAsync(conn, tx, t, needsApproval ? "cfg.change_set.submitted" : "cfg.change_set.created",
                "setting_change_set", setId.ToString(), after: new { request.Title, request.Changes }, operatorId: operatorId);
            if (!needsApproval)
            {
                await ApplyAsync(conn, tx, t, setId, operatorId);
            }
            return setId;
        }, ct);
        return await GetAsync(t, id, ct);
    }

    public async Task<ChangeSetDto> DecideAsync(TenantContext t, long id, bool approve, string? comment, CancellationToken ct)
    {
        t.Require(Permissions.CfgApprove);
        await db.TenantAsync(t, async (conn, tx) =>
        {
            var set = await conn.QuerySingleOrDefaultAsync<(long? OperatorId, string Status, Guid CreatedBy)>(
                "SELECT operator_id, status, created_by FROM bo.setting_change_set WHERE id = @id FOR UPDATE", new { id }, tx);
            if (set == default)
            {
                throw BoProblem.NotFound("Change set");
            }
            if (set.Status != "pending_approval")
            {
                throw BoProblem.Conflict("NOT_PENDING", $"Change set is {set.Status}");
            }
            if (set.CreatedBy == t.UserId)
            {
                throw BoProblem.Forbidden("FOUR_EYES", "A change set must be approved by someone else");
            }
            if (set.OperatorId is null)
            {
                t.Require(Permissions.PlatformSettingsEdit);
            }
            await conn.ExecuteAsync("""
                UPDATE bo.setting_change_set
                SET status = @status, decided_by = @user, decided_by_name = @name, decided_at = now(), decision_comment = @comment
                WHERE id = @id
                """, new { id, status = approve ? "applied" : "rejected", user = t.UserId, name = t.ActorName, comment }, tx);
            await Audit.WriteAsync(conn, tx, t, approve ? "cfg.change_set.approved" : "cfg.change_set.rejected",
                "setting_change_set", id.ToString(), reason: comment, operatorId: set.OperatorId);
            if (approve)
            {
                await ApplyAsync(conn, tx, t, id, set.OperatorId);
            }
        }, ct);
        return await GetAsync(t, id, ct);
    }

    private async Task ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantContext t, long setId, long? operatorId)
    {
        var changes = (await conn.QueryAsync<ChangeRow>(
            "SELECT id, op, scope_type AS scopetype, scope_id AS scopeid, market_type_id AS markettypeid, key, new_value::text AS newvalue FROM bo.setting_change WHERE change_set_id = @setId ORDER BY id",
            new { setId }, tx)).ToList();
        foreach (var c in changes)
        {
            // Old value as of apply time (an approval may come long after submission).
            var old = await CurrentValueAsync(conn, tx, operatorId, ScopeTypes.Parse(c.ScopeType), c.ScopeId, c.MarketTypeId, c.Key);
            if (c.Op == "delete")
            {
                await conn.ExecuteAsync("""
                    DELETE FROM bo.setting WHERE operator_id IS NOT DISTINCT FROM @operatorId AND scope_type = @scope
                      AND scope_id IS NOT DISTINCT FROM @scopeId AND market_type_id IS NOT DISTINCT FROM @mt AND key = @key
                    """, new { operatorId, scope = c.ScopeType, scopeId = c.ScopeId, mt = c.MarketTypeId, key = c.Key }, tx);
            }
            else
            {
                await conn.ExecuteAsync("""
                    INSERT INTO bo.setting (operator_id, scope_type, scope_id, market_type_id, key, value, change_set_id)
                    VALUES (@operatorId, @scope, @scopeId, @mt, @key, @value::jsonb, @setId)
                    ON CONFLICT (operator_id, scope_type, scope_id, market_type_id, key)
                    DO UPDATE SET value = EXCLUDED.value, change_set_id = EXCLUDED.change_set_id,
                                  version = bo.setting.version + 1, updated_at = now()
                    """, new { operatorId, scope = c.ScopeType, scopeId = c.ScopeId, mt = c.MarketTypeId, key = c.Key, value = c.NewValue, setId }, tx);
            }
            await conn.ExecuteAsync("UPDATE bo.setting_change SET old_value = @old::jsonb WHERE id = @id", new { old, id = c.Id }, tx);
        }

        await conn.ExecuteAsync("INSERT INTO bo.config_version (operator_id, version) VALUES (@operatorId, 0) ON CONFLICT (operator_id) DO NOTHING",
            new { operatorId }, tx);
        var version = await conn.ExecuteScalarAsync<long>(
            "UPDATE bo.config_version SET version = version + 1 WHERE operator_id IS NOT DISTINCT FROM @operatorId RETURNING version",
            new { operatorId }, tx);
        await conn.ExecuteAsync("UPDATE bo.setting_change_set SET status = 'applied', applied_at = now(), config_version = @version WHERE id = @setId",
            new { setId, version }, tx);
        await Audit.WriteAsync(conn, tx, t, "cfg.change_set.applied", "setting_change_set", setId.ToString(),
            after: new { version, changes = changes.Select(c => new { c.Op, c.ScopeType, c.ScopeId, c.MarketTypeId, c.Key }) }, operatorId: operatorId);
        await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId?.ToString() ?? "platform"}.cfg",
            new { version, changeSetId = setId, keys = changes.Select(c => c.Key).Distinct() });
        cache.Invalidate(operatorId);
    }

    public async Task<ChangeSetDto> GetAsync(TenantContext t, long id, CancellationToken ct)
    {
        t.Require(Permissions.CfgView);
        return (await ListAsync(t, null, id, 1, ct)).SingleOrDefault() ?? throw BoProblem.NotFound("Change set");
    }

    public Task<IReadOnlyList<ChangeSetDto>> ListAsync(TenantContext t, string? status, long? id, int limit, CancellationToken ct)
    {
        t.Require(Permissions.CfgView);
        return db.TenantAsync<IReadOnlyList<ChangeSetDto>>(t, async (conn, tx) =>
        {
            var sets = (await conn.QueryAsync<SetRow>("""
                SELECT id, operator_id AS operatorid, title, status, created_by AS createdby, created_by_name AS createdbyname, created_at AS createdat,
                       decided_by AS decidedby, decided_by_name AS decidedbyname, decided_at AS decidedat, decision_comment AS decisioncomment,
                       applied_at AS appliedat, config_version AS configversion
                FROM bo.setting_change_set
                WHERE (@id::bigint IS NULL OR id = @id) AND (@status::text IS NULL OR status = @status)
                ORDER BY id DESC LIMIT @limit
                """, new { id, status, limit }, tx)).ToList();
            var changes = (await conn.QueryAsync<ChangeDetailRow>("""
                SELECT change_set_id AS changesetid, op, scope_type AS scopetype, scope_id AS scopeid, market_type_id AS markettypeid, key,
                       old_value::text AS oldvalue, new_value::text AS newvalue
                FROM bo.setting_change WHERE change_set_id = ANY(@ids) ORDER BY id
                """, new { ids = sets.Select(s => s.Id).ToArray() }, tx)).ToLookup(c => c.ChangeSetId);
            return sets.Select(s => new ChangeSetDto(s.Id, s.OperatorId, s.Title, s.Status, s.CreatedBy, s.CreatedByName, s.CreatedAt,
                s.DecidedBy, s.DecidedByName, s.DecidedAt, s.DecisionComment, s.AppliedAt, s.ConfigVersion,
                changes[s.Id].Select(c => new SettingChangeDto(c.Op, c.ScopeType, c.ScopeId, c.MarketTypeId, c.Key, Parse(c.OldValue), Parse(c.NewValue))).ToList()))
                .ToList();
        }, ct);
    }

    /// <summary>Effective values for a context, with the trace of every candidate row (docs/06 §5.6 "why is X").</summary>
    public Task<IReadOnlyList<EffectiveSetting>> EffectiveAsync(TenantContext t, ScopeQuery query, IReadOnlyCollection<string>? keys, CancellationToken ct)
    {
        t.Require(Permissions.CfgView);
        var operatorId = t.RequireOperator();
        return db.TenantAsync<IReadOnlyList<EffectiveSetting>>(t, async (conn, tx) =>
        {
            var ctx = await ScopePaths.BuildAsync(conn, tx, operatorId, query);
            var rows = await cache.GetAsync(conn, tx, operatorId);
            var defs = keys is { Count: > 0 } ? SettingCatalog.All.Where(d => keys.Contains(d.Key)) : SettingCatalog.All;
            return SettingResolver.ResolveAll(defs, rows, ctx);
        }, ct);
    }

    /// <summary>Scope editor: every key allowed at the scope, what is set exactly here, and what applies here.</summary>
    public Task<IReadOnlyList<ScopeSettingDto>> ScopeAsync(TenantContext t, ScopeType scope, long? scopeId, int? marketTypeId, CancellationToken ct)
    {
        t.Require(Permissions.CfgView);
        long? operatorId = scope == ScopeType.Platform ? null : t.RequireOperator();
        if (scope == ScopeType.Platform)
        {
            t.RequirePlatform();
        }
        return db.TenantAsync<IReadOnlyList<ScopeSettingDto>>(t, async (conn, tx) =>
        {
            var query = ScopeQuery.ForScope(scope, scopeId, marketTypeId);
            var ctx = operatorId is { } op ? await ScopePaths.BuildAsync(conn, tx, op, query) : new ScopeContext(-1);
            var rows = operatorId is { } o ? await cache.GetAsync(conn, tx, o) : await cache.GetPlatformAsync(conn, tx);
            return SettingCatalog.All
                .Where(d => d.AllowedScopes.Contains(scope) && (marketTypeId is null || d.AllowsMarketType))
                .Select(d =>
                {
                    var here = rows.FirstOrDefault(r => r.Key == d.Key && r.ScopeType == scope && r.ScopeId == scopeId
                                                        && r.MarketTypeId == marketTypeId && r.OperatorId == operatorId);
                    var effective = SettingResolver.Resolve(d, rows, ctx);
                    return new ScopeSettingDto(d.Key, d.Module, here?.Value, here?.ChangeSetId, here?.UpdatedAt, effective);
                })
                .ToList();
        }, ct);
    }

    private static async Task EnsureScopeExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ScopeType scope, long? id, int? marketTypeId)
    {
        var sql = scope switch
        {
            ScopeType.Brand => "SELECT EXISTS (SELECT 1 FROM bo.brand WHERE id = @id)",   // RLS: own brands only
            ScopeType.Sport => "SELECT EXISTS (SELECT 1 FROM sb.sport WHERE id = @id)",
            ScopeType.Category => "SELECT EXISTS (SELECT 1 FROM sb.category WHERE id = @id)",
            ScopeType.Tournament => "SELECT EXISTS (SELECT 1 FROM sb.tournament WHERE id = @id)",
            ScopeType.Event => "SELECT EXISTS (SELECT 1 FROM sb.event WHERE id = @id)",
            ScopeType.Market => "SELECT EXISTS (SELECT 1 FROM sb.market WHERE id = @id)",
            _ => null,
        };
        if (sql is not null && !await conn.ExecuteScalarAsync<bool>(sql, new { id }, tx))
        {
            throw BoProblem.Invalid("UNKNOWN_SCOPE", $"{scope.ToDb()} {id} does not exist");
        }
        if (marketTypeId is { } mt && !await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM sb.market_description WHERE id = @mt)", new { mt }, tx))
        {
            throw BoProblem.Invalid("UNKNOWN_MARKET_TYPE", $"Market type {mt} does not exist");
        }
    }

    private static Task<string?> CurrentValueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long? operatorId, ScopeType scope, long? scopeId, int? mt, string key) =>
        conn.ExecuteScalarAsync<string?>("""
            SELECT value::text FROM bo.setting WHERE operator_id IS NOT DISTINCT FROM @operatorId AND scope_type = @scope
              AND scope_id IS NOT DISTINCT FROM @scopeId AND market_type_id IS NOT DISTINCT FROM @mt AND key = @key
            """, new { operatorId, scope = scope.ToDb(), scopeId, mt, key }, tx);

    private static JsonNode? Parse(string? json) => json is null ? null : JsonNode.Parse(json);

    private sealed class ChangeRow
    {
        public long Id { get; init; }
        public string Op { get; init; } = "";
        public string ScopeType { get; init; } = "";
        public long? ScopeId { get; init; }
        public int? MarketTypeId { get; init; }
        public string Key { get; init; } = "";
        public string? NewValue { get; init; }
    }

    private sealed class ChangeDetailRow
    {
        public long ChangeSetId { get; init; }
        public string Op { get; init; } = "";
        public string ScopeType { get; init; } = "";
        public long? ScopeId { get; init; }
        public int? MarketTypeId { get; init; }
        public string Key { get; init; } = "";
        public string? OldValue { get; init; }
        public string? NewValue { get; init; }
    }

    private sealed class SetRow
    {
        public long Id { get; init; }
        public long? OperatorId { get; init; }
        public string Title { get; init; } = "";
        public string Status { get; init; } = "";
        public Guid CreatedBy { get; init; }
        public string? CreatedByName { get; init; }
        public DateTime CreatedAt { get; init; }
        public Guid? DecidedBy { get; init; }
        public string? DecidedByName { get; init; }
        public DateTime? DecidedAt { get; init; }
        public string? DecisionComment { get; init; }
        public DateTime? AppliedAt { get; init; }
        public long? ConfigVersion { get; init; }
    }
}

public sealed record ScopeSettingDto(string Key, string Module, JsonNode? ValueHere, long? ChangeSetId, DateTime? UpdatedAt, EffectiveSetting Effective);

/// <summary>
/// Per-operator snapshot of setting rows (own + platform), reused while <c>config_version</c> is unchanged
/// (docs/06 §5.4). In-process for now; NATS invalidation and Valkey copies follow when other services need it.
/// </summary>
public sealed class SettingsSnapshotCache
{
    private readonly ConcurrentDictionary<long, Snapshot> _byOperator = new();
    private const long PlatformKey = -1;

    public void Invalidate(long? operatorId)
    {
        if (operatorId is null)
        {
            _byOperator.Clear();
        }
        else
        {
            _byOperator.TryRemove(operatorId.Value, out _);
        }
    }

    public Task<IReadOnlyList<SettingRow>> GetPlatformAsync(NpgsqlConnection conn, NpgsqlTransaction tx) => GetCoreAsync(conn, tx, null);

    public Task<IReadOnlyList<SettingRow>> GetAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long operatorId) => GetCoreAsync(conn, tx, operatorId);

    private async Task<IReadOnlyList<SettingRow>> GetCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long? operatorId)
    {
        var versions = (await conn.QueryAsync<(long? OperatorId, long Version)>(
            "SELECT operator_id, version FROM bo.config_version WHERE operator_id IS NULL OR operator_id = @operatorId",
            new { operatorId }, tx)).ToList();
        var platformVersion = versions.FirstOrDefault(v => v.OperatorId is null).Version;
        var operatorVersion = versions.FirstOrDefault(v => v.OperatorId is not null).Version;
        var key = operatorId ?? PlatformKey;
        if (_byOperator.TryGetValue(key, out var snap) && snap.PlatformVersion == platformVersion && snap.OperatorVersion == operatorVersion)
        {
            return snap.Rows;
        }
        var rows = (await conn.QueryAsync<RawRow>("""
            SELECT id, operator_id AS operatorid, scope_type AS scopetype, scope_id AS scopeid, market_type_id AS markettypeid,
                   key, value::text AS value, change_set_id AS changesetid, updated_at AS updatedat
            FROM bo.setting WHERE operator_id IS NULL OR operator_id = @operatorId
            """, new { operatorId }, tx))
            .Select(r => new SettingRow(r.Id, r.OperatorId, ScopeTypes.Parse(r.ScopeType), r.ScopeId, r.MarketTypeId, r.Key,
                JsonNode.Parse(r.Value)!, r.ChangeSetId, r.UpdatedAt))
            .ToList();
        _byOperator[key] = new Snapshot(platformVersion, operatorVersion, rows);
        return rows;
    }

    private sealed record Snapshot(long PlatformVersion, long OperatorVersion, IReadOnlyList<SettingRow> Rows);

    private sealed class RawRow
    {
        public long Id { get; init; }
        public long? OperatorId { get; init; }
        public string ScopeType { get; init; } = "";
        public long? ScopeId { get; init; }
        public int? MarketTypeId { get; init; }
        public string Key { get; init; } = "";
        public string Value { get; init; } = "";
        public long ChangeSetId { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
