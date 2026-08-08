using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Hyveman.Agent.Lifecycle;

/// <summary>
/// A single EventLog source "HyvemanAgent" for a handful of lifecycle/critical
/// messages (AGENT.md §12) so operators see them in the Application log
/// without the agent ingesting a chatty loop. The source itself is created by
/// install.ps1 — the agent never writes registry entries at runtime (§13, H6).
/// The self-collect channel (config entry "HyvemanAgent") ingests exactly
/// these IDs, allowlisted to prevent recursion.
/// </summary>
public static class EventLogLifecycle
{
    public const string SourceName = "HyvemanAgent";

    // Fixed event IDs, mirrored by the self-collect include_ids allowlist.
    public const int EventIdStarted = 1;
    public const int EventIdStopped = 2;
    public const int EventIdCritical = 3;
    public const int EventIdPreflightFail = 4;
    public const int EventIdRecoveryCap = 5;

    public static readonly int[] LifecycleEventIds = { EventIdStarted, EventIdStopped, EventIdCritical, EventIdPreflightFail, EventIdRecoveryCap };

    /// <summary>Writes a lifecycle event; never throws (missing source → file log only).</summary>
    public static void Write(int eventId, string message, EventLogEntryType type, ILogger? fileLog = null)
    {
        try
        {
            using var log = new EventLog("Application") { Source = SourceName };
            log.WriteEntry(message, type, eventId);
        }
        catch (Exception ex)
        {
            fileLog?.LogDebug(ex, "EventLog source '{source}' not registered; lifecycle message logged to file only", SourceName);
        }
    }

    public static bool SourceExists()
    {
        try
        {
            return EventLog.SourceExists(SourceName);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
