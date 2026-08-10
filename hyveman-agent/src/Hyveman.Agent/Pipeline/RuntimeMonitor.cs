using System.Collections.Concurrent;
using Hyveman.Agent.Options;

namespace Hyveman.Agent.Pipeline;

/// <summary>
/// Single source of truth for the counters surfaced in heartbeats
/// (AGENT.md §8) and the single-string `degraded` flag (§15).
/// </summary>
public sealed class RuntimeMonitor
{
    private long _eventsSent;
    private long _eventsDropped;
    private long _batchesSent;
    private long _batchesFailed;
    private long _wmiTimeouts;
    private long _channelResets;
    private long _quarantinedBatches;
    private long _replicationRelationships = -1; // -1 = scan not run / query unavailable

    private readonly ConcurrentQueue<long> _sendErrorTimes = new(); // Environment.TickCount64

    private readonly ConcurrentDictionary<string, long> _degradedFlags = new(StringComparer.Ordinal); // flag -> set-at (ticks)
    private readonly long _stickyTtlMs;

    public RuntimeMonitor(long stickyTtlMs = 2 * 60 * 1000) => _stickyTtlMs = stickyTtlMs;

    public void AddEventsSent(long n) => Interlocked.Add(ref _eventsSent, n);
    public void AddEventsDropped(long n) => Interlocked.Add(ref _eventsDropped, n);
    public void AddBatchesSent(long n) => Interlocked.Add(ref _batchesSent, n);
    public void AddBatchesFailed(long n) => Interlocked.Add(ref _batchesFailed, n);
    public void AddWmiTimeouts(long n) => Interlocked.Add(ref _wmiTimeouts, n);
    public void AddChannelResets(long n) => Interlocked.Add(ref _channelResets, n);
    public void AddQuarantinedBatches(long n) => Interlocked.Add(ref _quarantinedBatches, n);

    /// <summary>Msvm_ReplicationRelationship instances found in the last WMI
    /// scan; -1 when the scan has not run or the query was unavailable
    /// (non-Hyper-V host / class missing / query failure). Lets the backend
    /// distinguish "no replication configured" (0) from "can't query" (-1).</summary>
    public void SetReplicationRelationships(long n) => Interlocked.Exchange(ref _replicationRelationships, n);

    public void RecordSendError()
    {
        var now = Environment.TickCount64;
        _sendErrorTimes.Enqueue(now);
        // Opportunistic prune (cheap).
        while (_sendErrorTimes.TryPeek(out var oldest) && now - oldest > 60_000)
            _sendErrorTimes.TryDequeue(out _);
    }

    public long SendErrorsLastMinute
    {
        get
        {
            var now = Environment.TickCount64;
            while (_sendErrorTimes.TryPeek(out var oldest) && now - oldest > 60_000)
                _sendErrorTimes.TryDequeue(out _);
            return _sendErrorTimes.Count;
        }
    }

    // ---- degraded flags (AGENT §8, §15) ----

    public void SetDegraded(string flag) => _degradedFlags[flag] = Environment.TickCount64;

    public void ClearDegraded(string flag) => _degradedFlags.TryRemove(flag, out _);

    /// <summary>Priority-ordered single-string `degraded` value ("" when healthy).
    /// Flags are reported for a TTL window after being set (AGENT §8), so a
    /// transient saturation/overrun still reaches the next heartbeats, then
    /// self-clears without an agent restart.</summary>
    public string Degraded
    {
        get
        {
            var now = Environment.TickCount64;
            foreach (var key in new[] { "spool_full", "overrun", "auth_rejected", "quarantined", "wmi_degraded", "channel_reset" })
            {
                if (_degradedFlags.TryGetValue(key, out var setAt) && now - setAt <= _stickyTtlMs)
                    return key;
            }
            return "";
        }
    }

    public (long EventsSent, long EventsDropped, long BatchesSent, long BatchesFailed,
            long WmiTimeouts, long ChannelResets, long QuarantinedBatches, long SendErrorsLastMin,
            long ReplicationRelationships)
        Snapshot()
        => (Interlocked.Read(ref _eventsSent), Interlocked.Read(ref _eventsDropped),
            Interlocked.Read(ref _batchesSent), Interlocked.Read(ref _batchesFailed),
            Interlocked.Read(ref _wmiTimeouts), Interlocked.Read(ref _channelResets),
            Interlocked.Read(ref _quarantinedBatches), SendErrorsLastMinute,
            Interlocked.Read(ref _replicationRelationships));

    public HeartbeatCounters HeartbeatCounters(int queueDepth, long spoolBytes, int spoolFiles) => new()
    {
        EventsSent = Interlocked.Read(ref _eventsSent),
        EventsDropped = Interlocked.Read(ref _eventsDropped),
        BatchesSent = Interlocked.Read(ref _batchesSent),
        BatchesFailed = Interlocked.Read(ref _batchesFailed),
        SpoolBytes = spoolBytes,
        SpoolFiles = spoolFiles,
        QueueDepth = queueDepth,
        WmiTimeouts = Interlocked.Read(ref _wmiTimeouts),
        SendErrorsLastMin = SendErrorsLastMinute,
        ReplicationRelationships = Interlocked.Read(ref _replicationRelationships)
    };
}

public sealed class HeartbeatCounters
{
    public long EventsSent { get; set; }
    public long EventsDropped { get; set; }
    public long BatchesSent { get; set; }
    public long BatchesFailed { get; set; }
    public long SpoolBytes { get; set; }
    public int SpoolFiles { get; set; }
    public int QueueDepth { get; set; }
    public long WmiTimeouts { get; set; }
    public long SendErrorsLastMin { get; set; }
    public long ReplicationRelationships { get; set; } = -1;
}
