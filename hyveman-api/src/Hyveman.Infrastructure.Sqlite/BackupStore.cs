using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>VACUUM INTO hot snapshots + the 7 daily / 4 weekly / 12 monthly
/// retention ladder (API.md §9.5, DESIGN §9). Snapshots contain the existing
/// encrypted credential blobs; they are not re-encrypted in the MVP.</summary>
public sealed class BackupStore(SqliteDb db, string backupDirectory) : IBackupStore
{
    public async Task<BackupResult> CreateSnapshotAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(backupDirectory);
            var stamp = now.ToUniversalTime().ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(backupDirectory, $"hyveman-{stamp}.db");
            // VACUUM INTO does not accept bound parameters; the path is fully
            // under our control (data dir + timestamp), so escaping is enough.
            var escaped = path.Replace("'", "''");
            using var conn = StoreHelpers.Open(db);
            await conn.ExecuteAsync(new CommandDefinition($"VACUUM INTO '{escaped}'", cancellationToken: ct));
            var size = new FileInfo(path).Length;
            return new BackupResult(true, path, size, null);
        }
        catch (Exception ex)
        {
            return new BackupResult(false, "", 0, ex.Message);
        }
    }

    public async Task<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(backupDirectory)) return [];
        var files = Directory.GetFiles(backupDirectory, "hyveman-*.db")
            .Select(f => new FileInfo(f))
            .Where(f => DateTimeOffset.TryParseExact(f.Name[8..^3], "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _))
            .OrderBy(f => f.Name)
            .ToList();
        return files.Select(f => new BackupInfo(f.FullName,
            DateTimeOffset.ParseExact(f.Name[8..^3], "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal),
            f.Length, "daily")).ToList();
    }

    public async Task PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        var all = (await ListAsync(ct)).OrderByDescending(b => b.Time).ToList();
        var keep = new HashSet<string>();

        // Daily: newest per day, last 7 days.
        foreach (var group in all.Where(b => b.Time >= now.AddDays(-7)).GroupBy(b => b.Time.Date))
            keep.Add(group.OrderByDescending(b => b.Time).First().Path);

        // Weekly: newest per ISO week, last 4 weeks (older than 7 days).
        var isoWeek = (DateTimeOffset t) =>
            System.Globalization.ISOWeek.GetYear(t.DateTime) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(t.DateTime);
        foreach (var group in all.Where(b => b.Time < now.AddDays(-7) && b.Time >= now.AddDays(-35))
                     .GroupBy(b => isoWeek(b.Time)))
            keep.Add(group.OrderByDescending(b => b.Time).First().Path);

        // Monthly: newest per month, last 12 months (older than 35 days).
        foreach (var group in all.Where(b => b.Time < now.AddDays(-35))
                     .GroupBy(b => new DateTimeOffset(b.Time.Year, b.Time.Month, 1, 0, 0, 0, TimeSpan.Zero)))
            keep.Add(group.OrderByDescending(b => b.Time).First().Path);

        foreach (var backup in all)
        {
            if (!keep.Contains(backup.Path))
            {
                try
                {
                    File.Delete(backup.Path);
                }
                catch (IOException)
                {
                    // a concurrent backup may hold it; skip
                }
            }
        }
        await Task.CompletedTask;
    }
}
