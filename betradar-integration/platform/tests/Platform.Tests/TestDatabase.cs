using Npgsql;
using Platform.Canonical.Db;

namespace Platform.Tests;

/// <summary>
/// A throw-away database per test class. Set PLATFORM_TEST_PG to a PostgreSQL server connection string,
/// e.g. <c>Host=localhost;Port=55432;Username=postgres;Password=platform</c>; without it the DB tests are skipped.
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    public static string? ServerConnectionString => Environment.GetEnvironmentVariable("PLATFORM_TEST_PG");

    private readonly string _name = $"platform_test_{Guid.NewGuid():N}";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Full connection string (NpgsqlDataSource.ConnectionString omits the password).</summary>
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (ServerConnectionString is null)
        {
            return;
        }
        await using (var admin = NpgsqlDataSource.Create(ServerConnectionString))
        {
            await admin.CreateCommand($"CREATE DATABASE {_name}").ExecuteNonQueryAsync();
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = _name }.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await MigrationRunner.MigrateAsync(DataSource);
    }

    public async Task DisposeAsync()
    {
        if (ServerConnectionString is null)
        {
            return;
        }
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(ServerConnectionString);
        await admin.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)").ExecuteNonQueryAsync();
    }
}

public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (TestDatabase.ServerConnectionString is null)
        {
            Skip = "PLATFORM_TEST_PG not set";
        }
    }
}
