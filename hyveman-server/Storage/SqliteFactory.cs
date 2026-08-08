using Microsoft.Data.Sqlite;

namespace Hyveman.Server.Storage;

/// <summary>
/// SQLite connection factory applying the per-connection pragmas (§6.1).
/// Writes go through a single serialized <see cref="SqliteWriter"/>; readers use pooled connections.
/// </summary>
public sealed class SqliteFactory
{
    private readonly string _connectionString;

    public SqliteFactory(string dataDir)
    {
        var dbPath = Path.Combine(dataDir, "hyveman.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        ApplyPragmas(conn);
        return conn;
    }

    public SqliteConnection OpenReadOnly()
    {
        var cs = new SqliteConnectionStringBuilder(_connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        ApplyPragmas(conn);
        return conn;
    }

    private static void ApplyPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;
            """;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Serialized write access: one long-lived connection that all write paths share,
/// with explicit transaction scopes. WAL lets readers proceed concurrently.
/// </summary>
public sealed class SqliteWriter : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteWriter(SqliteFactory factory)
    {
        _conn = factory.Open();
    }

    /// <summary>Run <paramref name="work"/> inside a write transaction (serialized).</summary>
    public async Task<T> WithTransactionAsync<T>(Func<SqliteConnection, Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            await using var tx = await _conn.BeginTransactionAsync();
            var result = await work(_conn);
            await tx.CommitAsync();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WithTransactionAsync(Func<SqliteConnection, Task> work)
    {
        await WithTransactionAsync(async conn =>
        {
            await work(conn);
            return true;
        });
    }

    /// <summary>Run a read (or pragma) on the writer connection without a transaction.</summary>
    public async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            return await work(_conn);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _gate.Dispose();
    }
}
