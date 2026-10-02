using System.Reflection;
using Dapper;
using Npgsql;

namespace Platform.Canonical.Db;

/// <summary>
/// Applies the embedded <c>db/migrations/V*.sql</c> files in name order, once each, recording them in
/// <c>public.schema_migrations</c>. An advisory lock keeps concurrently starting services from racing.
/// </summary>
public static class MigrationRunner
{
    private const long LockKey = 0x5542_4F46; // "UBOF"

    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.V", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Resource: n, Version: n[(n.IndexOf(".Migrations.", StringComparison.Ordinal) + 12)..^4]))
            .OrderBy(m => m.Version, StringComparer.Ordinal)
            .ToList();

        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@LockKey)", new { LockKey });
        try
        {
            await conn.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS public.schema_migrations (
                  version    text PRIMARY KEY,
                  applied_at timestamptz NOT NULL DEFAULT now())
                """);
            var applied = (await conn.QueryAsync<string>("SELECT version FROM public.schema_migrations")).ToHashSet();

            var done = new List<string>();
            foreach (var (resource, version) in migrations.Where(m => !applied.Contains(m.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(Read(assembly, resource), transaction: tx);
                await conn.ExecuteAsync("INSERT INTO public.schema_migrations (version) VALUES (@version)", new { version }, tx);
                await tx.CommitAsync(ct);
                done.Add(version);
            }
            return done;
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@LockKey)", new { LockKey });
        }
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
