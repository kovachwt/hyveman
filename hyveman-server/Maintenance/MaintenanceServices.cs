using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Storage;
using Microsoft.Data.Sqlite;

namespace Hyveman.Server.Maintenance;

/// <summary>
/// Daily retention purge (§13.1): DELETE old events/metrics/snapshots/audit/resolved alerts,
/// then PRAGMA incremental_vacuum when configured.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly Db _db;
    private readonly ServerOptions _opts;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(Db db, ServerOptions opts, ILogger<RetentionService> logger)
    {
        _db = db;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once shortly after startup, then daily.
        await DelayToNextRun(TimeSpan.FromHours(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention purge failed");
            }
            await DelayToNextRun(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    private static async Task DelayToNextRun(TimeSpan initial, CancellationToken ct)
        => await Task.Delay(initial, ct);

    public async Task<Dictionary<string, long>> PurgeAsync(CancellationToken ct)
    {
        var r = _opts.Retention;
        var result = new Dictionary<string, long>();
        await _db.Writer.WithTransactionAsync(async conn =>
        {
            result["events"] = await DeleteOlderThanAsync(conn, "events", r.EventsDays, "time", ct);
            result["metrics"] = await DeleteOlderThanAsync(conn, "metrics", r.MetricsDays, "time", ct);
            result["health_snapshots"] = await DeleteOlderThanAsync(conn, "health_snapshots", r.HealthSnapshotsDays, "time", ct);
            result["audit_log"] = await DeleteOlderThanAsync(conn, "audit_log", r.AuditDays, "time", ct);
            result["resolved_alerts"] = await DeleteOlderThanAsync(conn, "alerts", r.ResolvedAlertsDays, "last_seen", ct,
                "AND status='resolved'");
        });

        if (r.VacuumAfterPurge && result.Values.Sum(v => v) > 0)
        {
            await using var conn = _db.Factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA incremental_vacuum;";
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Retention purge: {Counts}", string.Join(", ", result.Select(kv => $"{kv.Key}={kv.Value}")));
        return result;
    }

    private static async Task<long> DeleteOlderThanAsync(SqliteConnection conn, string table, int days, string timeCol,
        CancellationToken ct, string extra = "")
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        // "YYYY-MM-DDTHH:MM:SSZ" vs stored "YYYY-MM-DDTHH:MM:SS.fffZ" — compare on date prefix to be safe.
        var cutoffDay = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        var sql = $"DELETE FROM {table} WHERE {timeCol} < @cutoffDay {extra}";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@cutoffDay", cutoffDay);
        return (long)await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Daily VACUUM INTO snapshot + retention ladder (§13.2, DESIGN §9). Snapshots contain only
/// ciphertext secrets; restore needs snapshot + key K.
/// </summary>
public sealed class BackupService : BackgroundService
{
    private readonly Db _db;
    private readonly string _dataDir;
    private readonly ServerOptions _opts;
    private readonly ILogger<BackupService> _logger;

    public BackupService(Db db, string dataDir, ServerOptions opts, ILogger<BackupService> logger)
    {
        _db = db;
        _dataDir = dataDir;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NextRun(TimeOnly.Parse(_opts.Backup.TimeLocal));
            var delay = next - DateTimeOffset.Now;
            _logger.LogInformation("Next backup at {Next:o}", next);
            try
            {
                await Task.Delay(delay, stoppingToken);
                await RunBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup failed");
            }
        }
    }

    private static DateTimeOffset NextRun(TimeOnly localTime)
    {
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, localTime.Hour, localTime.Minute, 0, now.Offset);
        return today <= now ? today.AddDays(1) : today;
    }

    public async Task<BackupResult> RunBackupAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var dailyDir = Path.Combine(_dataDir, "backup", "daily");
        var weeklyDir = Path.Combine(_dataDir, "backup", "weekly");
        var monthlyDir = Path.Combine(_dataDir, "backup", "monthly");
        Directory.CreateDirectory(dailyDir); Directory.CreateDirectory(weeklyDir); Directory.CreateDirectory(monthlyDir);

        var dailyFile = Path.Combine(dailyDir, $"hyveman-{now:yyyyMMdd}.db");
        var size = 0L;

        // Clean up snapshots from older builds that used an invalid week format.
        foreach (var f in Directory.GetFiles(weeklyDir, "hyveman-*WW.db")) File.Delete(f);

        // 1. VACUUM INTO — the only safe hot copy (WAL-safe, crash-safe).
        await _db.Writer.ReadAsync(async conn =>
        {
            var path = dailyFile.Replace("'", "''");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{path}';";
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        });
        size = new FileInfo(dailyFile).Length;

        // 2. Promotion ladder: newest of the week → weekly, newest of the month → monthly.
        Promote(now, dailyDir, weeklyDir, $"hyveman-{System.Globalization.ISOWeek.GetYear(now.DateTime)}W{System.Globalization.ISOWeek.GetWeekOfYear(now.DateTime):D2}.db", ct);
        Promote(now, dailyDir, monthlyDir, $"hyveman-{now:yyyyMM}.db", ct);

        // 3. Prune beyond keep counts.
        var pruned = Prune(dailyDir, _opts.Backup.KeepDaily);
        pruned += Prune(weeklyDir, _opts.Backup.KeepWeekly);
        pruned += Prune(monthlyDir, _opts.Backup.KeepMonthly);

        await _db.Audit.WriteAsync("system", "backup.run", "backup", null,
            System.Text.Json.JsonSerializer.Serialize(new { file = Path.GetFileName(dailyFile), bytes = size, pruned }));
        Observability.OwnMetrics.BackupLast = (now, size);
        _logger.LogInformation("Backup complete: {File} ({Bytes} bytes, pruned {Pruned})", dailyFile, size, pruned);
        return new BackupResult(dailyFile, size, pruned);
    }

    private static void Promote(DateTimeOffset now, string srcDir, string destDir, string destFileName, CancellationToken ct)
    {
        var candidates = Directory.GetFiles(srcDir, "hyveman-*.db")
            .Where(f => Path.GetFileName(f).StartsWith($"hyveman-{now:yyyyMM}") && Path.GetFileName(f) != destFileName)
            .OrderBy(f => f)
            .ToList();
        if (candidates.Count == 0) return;
        var latest = candidates[^1];
        var dest = Path.Combine(destDir, destFileName);
        var destInfo = new FileInfo(dest);
        var latestInfo = new FileInfo(latest);
        if (!destInfo.Exists || destInfo.LastWriteTimeUtc < latestInfo.LastWriteTimeUtc)
            File.Copy(latest, dest, overwrite: true);
    }

    private static int Prune(string dir, int keep)
    {
        var files = Directory.GetFiles(dir, "hyveman-*.db").OrderByDescending(f => f).ToList();
        var removed = 0;
        foreach (var f in files.Skip(keep))
        {
            try { File.Delete(f); removed++; } catch (IOException) { }
        }
        return removed;
    }

    public sealed record BackupResult(string File, long Bytes, int Pruned);
}

/// <summary>Periodically evict stale rate-limit buckets (§3.2).</summary>
public sealed class RateLimitReaper : BackgroundService
{
    private readonly RateLimit.RateLimiter _limiter;
    private readonly ILogger<RateLimitReaper> _logger;

    public RateLimitReaper(RateLimit.RateLimiter limiter, ILogger<RateLimitReaper> logger)
    {
        _limiter = limiter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _limiter.Reap(TimeSpan.FromMinutes(10));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
