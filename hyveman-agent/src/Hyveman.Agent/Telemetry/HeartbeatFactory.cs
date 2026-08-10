using System.Globalization;
using Hyveman.Agent.Net;
using Hyveman.Agent.Options;
using Hyveman.Agent.Pipeline;

namespace Hyveman.Agent.Telemetry;

/// <summary>
/// Builds the heartbeat envelope (AGENT.md §8): version, OS build, boot time,
/// uptime, free disk, counters, degraded flag, config hash.
/// </summary>
public static class HeartbeatFactory
{
    public static HeartbeatItem Build(OptionsSnapshot snapshot, RuntimeMonitor monitor, BoundedQueue<Hyveman.Agent.Wevtapi.EvtLogEvent> queue, string spoolDir)
    {
        var opts = snapshot.Active;
        var counters = monitor.Snapshot();

        var (spoolBytes, spoolFiles) = SpoolDirectory.Measure(spoolDir);

        return new HeartbeatItem
        {
            SentAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            AgentVersion = AgentInfo.Version,
            ProtocolVersion = 1,
            OsBuild = AgentInfo.OsBuild,
            BootTime = AgentInfo.BootTimeUtc?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            UptimeS = AgentInfo.UptimeSeconds,
            FreeDisk = FreeDiskInfo(),
            SourceId = opts.SourceId,
            Counters = new HeartbeatCountersWire
            {
                EventsSent = counters.EventsSent,
                EventsDropped = counters.EventsDropped,
                BatchesSent = counters.BatchesSent,
                BatchesFailed = counters.BatchesFailed,
                SpoolBytes = spoolBytes,
                SpoolFiles = spoolFiles,
                QueueDepth = queue.Count,
                WmiTimeouts = counters.WmiTimeouts,
                SendErrorsLastMin = counters.SendErrorsLastMin
            },
            Degraded = monitor.Degraded,
            ConfigHash = snapshot.ConfigHash
        };
    }

    /// <summary>Every fixed volume (AGENT §8), so the backend can alert on any
    /// drive (OS, spool, VHD datastores, ...). USB/optical/network drives are
    /// excluded; a fixed drive that is not ready is skipped, never sampled.</summary>
    private static List<FreeDisk> FreeDiskInfo()
    {
        var list = new List<FreeDisk>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var total = drive.TotalSize;
                list.Add(new FreeDisk
                {
                    Path = drive.Name,
                    Bytes = drive.AvailableFreeSpace,
                    Pct = total > 0 ? (double)drive.AvailableFreeSpace / total : 0
                });
            }
        }
        catch (Exception)
        {
            // disk info must never break the heartbeat
        }
        return list;
    }
}

/// <summary>Agent/OS identity facts (AGENT §8).</summary>
public static class AgentInfo
{
    public static readonly string Version =
        typeof(AgentInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static readonly string OsBuild = GetOsBuild();

    public static readonly long UptimeSeconds = Environment.TickCount64 / 1000;

    public static readonly DateTime? BootTimeUtc = DateTime.UtcNow - TimeSpan.FromSeconds(UptimeSeconds);

    private static string GetOsBuild()
    {
        try
        {
            return Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return "0";
        }
    }
}
