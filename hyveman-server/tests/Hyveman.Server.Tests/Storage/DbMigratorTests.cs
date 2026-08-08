using Hyveman.Server.Storage;
using Hyveman.Server.Tests.TestInfra;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyveman.Server.Tests.Storage;

/// <summary>Migration integrity (§6.5): all embedded migrations apply in order, idempotently.</summary>
public sealed class DbMigratorTests
{
    [Fact]
    public async Task Migrate_AppliesAllEmbeddedMigrations()
    {
        using var db = await TestDb.CreateAsync();

        var versions = await db.Db.Writer.ReadAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version FROM schema_migrations ORDER BY version";
            var list = new List<int>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(r.GetInt32(0));
            return list;
        });

        Assert.Equal(new[] { 1, 2 }, versions);
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        using var db = await TestDb.CreateAsync();
        var migrator = new DbMigrator(db.Factory, NullLogger<DbMigrator>.Instance);

        await migrator.MigrateAsync(); // second run over an already-migrated DB

        var count = await db.Db.Writer.ReadAsync(async conn =>
            (int)(long)(await new SqliteCommand("SELECT COUNT(*) FROM schema_migrations", conn).ExecuteScalarAsync())!);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Migrate_CreatesAllExpectedTables()
    {
        using var db = await TestDb.CreateAsync();

        var tables = await db.Db.Writer.ReadAsync(async conn =>
        {
            var names = new List<string>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view')";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) names.Add(r.GetString(0));
            return names;
        });

        foreach (var expected in new[]
        {
            "sources", "hosts", "tokens", "events", "events_fts", "components",
            "health_snapshots", "metrics", "vms", "agent_heartbeats", "rules", "alerts",
            "rule_channels", "notification_channels", "notification_queue", "logon_stats",
            "maintenance_windows", "passkeys", "credentials", "audit_log", "schema_migrations",
        })
        {
            Assert.Contains(expected, tables);
        }
    }
}
