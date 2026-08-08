using System.Collections.Concurrent;
using System.Diagnostics;

namespace Hyveman.Server.Observability;

/// <summary>
/// In-memory self-observability counters (§14): ingest accepted/deduped/rejected, poller
/// latency per host, alert/notification counts, backup status. Surfaced in the /admin UI.
/// </summary>
public sealed class OwnMetrics
{
    public long IngestAccepted;
    public long IngestDeduped;
    public long IngestRejected;
    public long LogBatches;
    public long Heartbeats;
    public long FactsBatches;
    public long AlertsFired;
    public long AlertsResolved;
    public long NotificationsQueued;
    public long NotificationsSent;
    public long AuthFailures;

    public void LogIngest(int accepted, int deduped, int rejected)
    {
        Interlocked.Add(ref IngestAccepted, accepted);
        Interlocked.Add(ref IngestDeduped, deduped);
        Interlocked.Add(ref IngestRejected, rejected);
        Interlocked.Increment(ref LogBatches);
    }

    public void Heartbeat() => Interlocked.Increment(ref Heartbeats);
    public void FactsBatch() => Interlocked.Increment(ref FactsBatches);
    public void AlertFired() => Interlocked.Increment(ref AlertsFired);
    public void AlertResolved() => Interlocked.Increment(ref AlertsResolved);
    public void NotificationQueued(int n) => Interlocked.Add(ref NotificationsQueued, n);
    public void NotificationSent() => Interlocked.Increment(ref NotificationsSent);
    public void AuthFailure() => Interlocked.Increment(ref AuthFailures);

    public static (DateTimeOffset at, long bytes)? BackupLast;

    public readonly ConcurrentDictionary<string, (long count, long totalMs)> PollerLatency = new();
    public long PollerFailures;

    public void PollSuccess(string hostId, long ms)
        => PollerLatency.AddOrUpdate(hostId, (1, ms), (_, v) => (v.count + 1, v.totalMs + ms));

    public void PollFailure(string hostId) => PollerFailures++;

    public (long dbBytes, long walBytes, int events, int sources, int hosts, int alerts) Snapshot(Storage.Db db, string dataDir)
    {
        try
        {
            long dbBytes = 0, walBytes = 0;
            var dbFile = Path.Combine(dataDir, "hyveman.db");
            if (File.Exists(dbFile)) dbBytes = new FileInfo(dbFile).Length;
            var wal = dbFile + "-wal";
            if (File.Exists(wal)) walBytes = new FileInfo(wal).Length;

            using var conn = db.Factory.OpenReadOnly();
            long events, sources, hosts, alerts;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT (SELECT COUNT(*) FROM events), (SELECT COUNT(*) FROM sources), (SELECT COUNT(*) FROM hosts), (SELECT COUNT(*) FROM alerts WHERE status='active')";
                using var r = cmd.ExecuteReader();
                r.Read();
                events = r.GetInt64(0); sources = r.GetInt64(1); hosts = r.GetInt64(2); alerts = r.GetInt64(3);
            }
            return (dbBytes, walBytes, (int)events, (int)sources, (int)hosts, (int)alerts);
        }
        catch (Exception)
        {
            return (0, 0, 0, 0, 0, 0);
        }
    }
}
