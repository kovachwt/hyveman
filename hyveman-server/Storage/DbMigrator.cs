using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Hyveman.Server.Storage;

/// <summary>
/// In-order, idempotent SQL migrations (§6.5): embedded .sql resources applied inside a
/// transaction, tracked in schema_migrations(version). Additive-only.
/// </summary>
public sealed class DbMigrator
{
    private readonly SqliteFactory _factory;
    private readonly ILogger<DbMigrator> _logger;

    public DbMigrator(SqliteFactory factory, ILogger<DbMigrator> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var migrations = LoadMigrations();
        await using var conn = _factory.Open();
        await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')))");

        var applied = new HashSet<int>(await conn.QueryAsync<int>("SELECT version FROM schema_migrations"));
        foreach (var (version, sql) in migrations.OrderBy(m => m.Version))
        {
            ct.ThrowIfCancellationRequested();
            if (applied.Contains(version)) continue;
            _logger.LogInformation("Applying schema migration {Version}", version);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync("INSERT INTO schema_migrations(version) VALUES (@v)", new { v = version }, transaction: tx);
            await tx.CommitAsync(ct);
        }
    }

    private static List<(int Version, string Sql)> LoadMigrations()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<(int, string)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var leaf = name.Split('.').Reverse().Skip(1).FirstOrDefault() ?? name;
            // Resource names are flattened: Hyveman.Server.Storage.Migrations.0001_initial.sql
            var match = System.Text.RegularExpressions.Regex.Match(name, @"Migrations\.(\d+)_[\w]+\.sql$");
            if (!match.Success) continue;
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration {name} not found");
            using var reader = new StreamReader(stream);
            list.Add((int.Parse(match.Groups[1].Value), reader.ReadToEnd()));
        }
        return list;
    }
}
