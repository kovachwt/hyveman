using Hyveman.Domain;
using Microsoft.Extensions.Logging;

namespace Hyveman.Application;

/// <summary>Retention, backup and housekeeping job (API.md §9.5): event/metric
/// purges, FTS maintenance, VACUUM INTO snapshots with the 7/4/12 ladder, and
/// cleanup of expired sessions/challenges/windows.</summary>
public sealed class MaintenanceJob(
    IEventStore events,
    IHealthStore health,
    IBackupStore backups,
    ISessionStore sessions,
    ICeremonyStore ceremonies,
    IMaintenanceWindowStore windows,
    ISettingsStore settings,
    IClock clock,
    ILogger<MaintenanceJob> log) : IMaintenanceJob
{
    public async Task RunRetentionAsync(DateTimeOffset now, CancellationToken ct)
    {
        var retention = await ReadRetentionAsync(ct);
        var eventCutoff = now.AddDays(-retention.EventDays);
        var metricCutoff = now.AddDays(-retention.MetricDays);
        var snapshotCutoff = now.AddDays(-retention.SnapshotDays);

        var purgedEvents = await events.PurgeOlderThanAsync(eventCutoff, ct);
        var purgedMetrics = await health.PurgeMetricsOlderThanAsync(metricCutoff, ct);
        var purgedSnapshots = await health.PurgeSnapshotsOlderThanAsync(snapshotCutoff, ct);
        if (purgedEvents > 0 || purgedMetrics > 0 || purgedSnapshots > 0)
            log.LogInformation("Retention purge: {events} events, {metrics} metrics, {snapshots} snapshots",
                purgedEvents, purgedMetrics, purgedSnapshots);
    }

    public async Task RunBackupAsync(DateTimeOffset now, CancellationToken ct)
    {
        var result = await backups.CreateSnapshotAsync(now, ct);
        if (result.Ok)
            log.LogInformation("Backup snapshot written: {path} ({size} bytes)", result.Path, result.SizeBytes);
        else
            log.LogError("Backup snapshot FAILED: {error}", result.Error);
        await backups.PruneAsync(now, ct);
    }

    public async Task RunCleanupAsync(DateTimeOffset now, CancellationToken ct)
    {
        await sessions.CleanupExpiredAsync(now, ct);
        await ceremonies.CleanupExpiredAsync(now, ct);
        await windows.DeleteExpiredAsync(now, ct);
    }

    private async Task<Hyveman.Contracts.RetentionSettingsDto> ReadRetentionAsync(CancellationToken ct)
    {
        var raw = await settings.GetAsync(SettingsService.Key, ct);
        if (raw is null) return new Hyveman.Contracts.RetentionSettingsDto();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Hyveman.Contracts.RetentionSettingsDto>(raw)
                ?? new Hyveman.Contracts.RetentionSettingsDto();
        }
        catch (System.Text.Json.JsonException)
        {
            return new Hyveman.Contracts.RetentionSettingsDto();
        }
    }
}

/// <summary>Agent-silence evaluation (API.md §9.2). Runs independently of
/// telemetry requests; uses the server receive time of the last heartbeat.
/// The hosted wrapper ticks this periodically.</summary>
public sealed class HeartbeatMonitor(
    IAgentStatusStore agentStatus,
    IRuleStore rules,
    IAlertEvaluator evaluator,
    IMaintenanceWindowStore windows,
    IHostStore hosts,
    IClock clock,
    ILogger<HeartbeatMonitor> log)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;
        var hbRules = (await rules.ListAsync(ct))
            .Where(r => r.Enabled && r.Type == RuleTypes.Heartbeat).ToList();
        if (hbRules.Count == 0) return;

        var statuses = await agentStatus.ListAllAsync(ct);
        if (statuses.Count == 0) return;
        var hostBySource = (await hosts.ListAsync(ct)).Where(h => h.SourceId is not null)
            .ToDictionary(h => h.SourceId!, h => h.Id);

        foreach (var status in statuses)
        {
            foreach (var rule in hbRules)
            {
                var match = RuleMatch.ParseHeartbeat(rule.MatchJson);
                if (match is null) continue;
                var silent = at - status.LastReceived > TimeSpan.FromSeconds(match.SilenceAfterS);
                if (!silent) continue;
                if (hostBySource.TryGetValue(status.SourceId, out var hostId)
                    && await windows.IsInWindowAsync(hostId, at, ct))
                    continue; // maintenance suppresses silence alerts
                await evaluator.OnHeartbeatSilenceChangedAsync(rule.Id, status.SourceId, silent: true, at, ct);
            }
        }
    }
}
