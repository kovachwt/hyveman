using Hyveman.Server.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyveman.Server.Tests.TestInfra;

/// <summary>
/// A fully migrated SQLite sandbox in a temp directory, disposed (and deleted) per test.
/// Each test gets its own DB — no shared state, safe to parallelize.
/// </summary>
public sealed class TestDb : IDisposable
{
    public string DataDir { get; }
    public SqliteFactory Factory { get; }
    public SqliteWriter Writer { get; }
    public Db Db { get; }

    private TestDb(string dataDir, SqliteFactory factory, SqliteWriter writer, Db db)
    {
        DataDir = dataDir;
        Factory = factory;
        Writer = writer;
        Db = db;
    }

    public static async Task<TestDb> CreateAsync()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "hyveman-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var factory = new SqliteFactory(dataDir);
        var writer = new SqliteWriter(factory);
        try
        {
            var migrator = new DbMigrator(factory, NullLogger<DbMigrator>.Instance);
            await migrator.MigrateAsync();
            return new TestDb(dataDir, factory, writer, new Db(factory, writer));
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Writer.Dispose();
        // WAL/shm files may linger briefly after the writer disposes; retry before giving up.
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(DataDir, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
