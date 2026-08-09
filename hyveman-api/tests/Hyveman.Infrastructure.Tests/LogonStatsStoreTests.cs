using Dapper;
using Hyveman.Application;
using Hyveman.Domain;
using Hyveman.Infrastructure.Sqlite;
using Xunit;

namespace Hyveman.Tests.Infrastructure;

public class LogonStatsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteDb _db;
    private readonly LogonStatsStore _store;

    public LogonStatsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hyveman-logon-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_dir);
        _db = new SqliteDb(Path.Combine(_dir, "test.db"));
        _db.Migrate();
        _store = new LogonStatsStore(_db);
        using var conn = _db.Open();
        conn.Execute("""
            INSERT INTO sources(id, kind, name, created_at) VALUES
            ('src_1', 'windows-agent', 'HOST01', '2024-01-01T00:00:00.0000000Z'),
            ('src_2', 'windows-agent', 'HOST02', '2024-01-01T00:00:00.0000000Z');
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Increment_MergesSameKey_AcrossBatches()
    {
        await _store.IncrementAsync("src_1",
            [new LogonStatEntry("2024-08-07", "admin", 10, 1, 0)], CancellationToken.None);
        await _store.IncrementAsync("src_1",
            [new LogonStatEntry("2024-08-07", "admin", 10, 1, 0), new LogonStatEntry("2024-08-07", "admin", 10, 0, 1)],
            CancellationToken.None);

        var rows = await _store.QueryAsync(new LogonStatsQuery(null, null, "src_1", null, 50), CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal("admin", row.User);
        Assert.Equal(10, row.LogonType);
        Assert.Equal(2, row.SuccessCount);
        Assert.Equal(1, row.FailureCount);
    }

    [Fact]
    public async Task Increment_NullLogonType_MergesInsteadOfDuplicating()
    {
        // 4740 lockouts have NULL logon_type; SQLite NULLs are distinct in
        // unique indexes, so the store must update-then-insert, not ON CONFLICT.
        for (var i = 0; i < 3; i++)
        {
            await _store.IncrementAsync("src_1",
                [new LogonStatEntry("2024-08-07", "bob", null, 0, 1)], CancellationToken.None);
        }

        var rows = await _store.QueryAsync(new LogonStatsQuery(null, null, "src_1", null, 50), CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Null(row.LogonType);
        Assert.Equal(3, row.FailureCount);
    }

    [Fact]
    public async Task Query_FiltersAndOrders()
    {
        await _store.IncrementAsync("src_1",
            [
                new LogonStatEntry("2024-08-06", "admin", 10, 1, 0),
                new LogonStatEntry("2024-08-07", "admin", 10, 2, 0),
                new LogonStatEntry("2024-08-07", "bob", null, 0, 2),
            ], CancellationToken.None);
        await _store.IncrementAsync("src_2",
            [new LogonStatEntry("2024-08-07", "admin", 10, 1, 0)], CancellationToken.None);

        // By source.
        var bySource = await _store.QueryAsync(new LogonStatsQuery(null, null, "src_2", null, 50), CancellationToken.None);
        var s2 = Assert.Single(bySource);
        Assert.Equal("HOST02", s2.SourceName);

        // By day range (inclusive whole-day strings): src_1 has two rows and
        // src_2 one row on 2024-08-07.
        var ranged = await _store.QueryAsync(new LogonStatsQuery(
            DateTimeOffset.Parse("2024-08-07T00:00:00Z"), DateTimeOffset.Parse("2024-08-07T23:59:59Z"),
            null, null, 50), CancellationToken.None);
        Assert.Equal(3, ranged.Count);

        // By user.
        var byUser = await _store.QueryAsync(new LogonStatsQuery(null, null, null, "bob", 50), CancellationToken.None);
        var bob = Assert.Single(byUser);
        Assert.Equal(2, bob.FailureCount);

        // Limit.
        var limited = await _store.QueryAsync(new LogonStatsQuery(null, null, "src_1", null, 2), CancellationToken.None);
        Assert.Equal(2, limited.Count);
    }
}
