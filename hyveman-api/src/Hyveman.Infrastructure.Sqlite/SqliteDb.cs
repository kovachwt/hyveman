using Microsoft.Data.Sqlite;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>SQLite connection factory: WAL mode, foreign keys on, bounded busy
/// timeout, explicit migrations (API.md §10).</summary>
public sealed class SqliteDb
{
    public string DbPath { get; }
    private readonly string _connectionString;
    private readonly int _busyTimeoutMs;

    public SqliteDb(string dbPath, int busyTimeoutMs = 5000)
    {
        DbPath = dbPath;
        _busyTimeoutMs = busyTimeoutMs;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutMs}; PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Applies pending migrations and sets WAL journal mode.</summary>
    public void Migrate()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations(
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        foreach (var (version, sql) in Migrations.All)
        {
            var applied = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM schema_migrations WHERE version = $v";
                cmd.Parameters.AddWithValue("$v", version);
                applied = cmd.ExecuteScalar() is not null;
            }
            if (applied) continue;
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES ($v, $t)";
                cmd.Parameters.AddWithValue("$v", version);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString(TimeFormat.Full));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}

/// <summary>Fixed UTC timestamp format: ISO-8601, lexicographically sortable.</summary>
public static class TimeFormat
{
    public const string Full = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    public const string Day = "yyyy-MM-dd";
}
