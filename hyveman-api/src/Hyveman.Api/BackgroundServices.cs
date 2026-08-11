using System.Text.Json;
using Hyveman.Application;
using Hyveman.Infrastructure.Security;
using Hyveman.Infrastructure.Sqlite;
using Hyveman.Domain;

namespace Hyveman.Api;

/// <summary>Readiness: the API is ready only when the database and the vault
/// key are available (API.md §11 startup sequence, §10.1).</summary>
public interface IReadinessCheck
{
    Task<bool> IsReadyAsync(CancellationToken ct);
}

public sealed class ReadinessCheck(IServiceProvider services, ILogger<ReadinessCheck> log) : IReadinessCheck
{
    private bool _lastOk = true;

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteScalar();
            var vault = scope.ServiceProvider.GetRequiredService<CredentialVault>();
            vault.CheckKey();
            _lastOk = true;
            return true;
        }
        catch (Exception ex)
        {
            if (_lastOk) log.LogError(ex, "Readiness check failed");
            _lastOk = false;
            return false;
        }
    }
}

/// <summary>Hardware poller (API.md §9.1): schedules registered hosts
/// independently with per-host backoff; a failed poll records the failure
/// without erasing the last known component state.</summary>
public sealed class HardwarePollingService(
    IServiceScopeFactory scopes,
    HyvemanOptions opts,
    IClock clock,
    ILogger<HardwarePollingService> log) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _nextDue = new();
    private readonly Dictionary<string, int> _failures = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Hardware polling tick failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var hosts = scope.ServiceProvider.GetRequiredService<IHostStore>();
        var provider = scope.ServiceProvider.GetRequiredService<IHardwareProvider>();
        var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();
        var health = scope.ServiceProvider.GetRequiredService<IHealthStore>();
        var pollStatus = scope.ServiceProvider.GetRequiredService<IPollStatusStore>();
        var evaluator = scope.ServiceProvider.GetRequiredService<IAlertEvaluator>();

        var now = clock.UtcNow;
        foreach (var host in await hosts.ListAsync(ct))
        {
            if (!host.Enabled || string.IsNullOrEmpty(host.IdracUrl) || host.IdracCredRef is null)
                continue;
            var due = _nextDue.GetValueOrDefault(host.Id, DateTimeOffset.MinValue);
            if (now < due) continue;

            string? error = null;
            try
            {
                var credsJson = await vault.LoadAsync(host.IdracCredRef, ct);
                if (credsJson is null)
                {
                    error = "iDRAC credentials unavailable";
                    await pollStatus.MarkFailureAsync(host.Id, now, error, ct);
                    ScheduleRetry(host.Id, now);
                    continue;
                }
                using var creds = JsonDocument.Parse(credsJson);
                var username = creds.RootElement.GetProperty("username").GetString() ?? "";
                var password = creds.RootElement.GetProperty("password").GetString() ?? "";

                var result = await provider.PollAsync(new HardwarePollTarget(
                    host.Id, host.Name, host.IdracUrl, username, password), ct);
                if (!result.Success)
                {
                    await pollStatus.MarkFailureAsync(host.Id, now, result.Error, ct);
                    ScheduleRetry(host.Id, now);
                    log.LogWarning("Poll failed for {host}: {error}", host.Name, result.Error);
                    continue;
                }

                // Evaluate transitions BEFORE replacing stored component state:
                // the evaluator reads the previous poll from the store (DEFECTS.md
                // D3). A derived-alerting failure must not lose the accepted poll
                // result, so it is contained here rather than in the poll catch.
                try
                {
                    await evaluator.OnHealthStateChangedAsync(host.Id, result.RollupState, result.Components, now, ct);
                    await evaluator.OnThresholdsAsync(host.Id, result.Metrics, now, ct);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Alert evaluation failed for host {host}; poll result still stored", host.Name);
                }

                await health.ReplaceComponentsAsync(host.Id, result.Components, ct);
                var componentsJson = JsonSerializer.Serialize(result.Components.Select(c => new
                {
                    type = c.Type, name = c.Name, state = HealthStates.ToWire(c.State), c.Detail,
                }));
                await health.AddSnapshotAsync(host.Id, result.PolledAt, result.RollupState, componentsJson, ct);
                await health.AddMetricsAsync(host.Id, result.PolledAt, result.Metrics, ct);
                await pollStatus.MarkSuccessAsync(host.Id, now, ct);
                _failures[host.Id] = 0;
                _nextDue[host.Id] = now.Add(opts.HardwarePollInterval);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                await pollStatus.MarkFailureAsync(host.Id, now, error, ct);
                ScheduleRetry(host.Id, now);
                log.LogWarning("Poll exception for {host}: {error}", host.Name, ex.Message);
            }
        }
    }

    private void ScheduleRetry(string hostId, DateTimeOffset now)
    {
        var failures = _failures.GetValueOrDefault(hostId, 0) + 1;
        _failures[hostId] = failures;
        // Exponential backoff capped at 30 minutes so one unreachable iDRAC
        // cannot consume all worker capacity (API.md §9.1).
        var backoff = TimeSpan.FromSeconds(Math.Min(30 * 60, Math.Pow(2, Math.Min(failures, 10)) * 10));
        _nextDue[hostId] = now.Add(backoff);
    }
}

/// <summary>Heartbeat monitor (API.md §9.2): evaluates receive-time age of each
/// source's last heartbeat against the configured heartbeat rules.</summary>
public sealed class HeartbeatMonitorService(
    IServiceScopeFactory scopes,
    TimeSpan interval,
    ILogger<HeartbeatMonitorService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<HeartbeatMonitor>().RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Heartbeat monitor tick failed");
            }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>Alert auto-resolve (API.md §9.3): periodically resolves live
/// alerts whose rule has an auto-resolve timeout and whose last occurrence is
/// older than the window (event/logon rules are fire-and-forget — the timeout
/// replaces the manual ack for transient noise). Every tick runs in a fresh
/// scope with a fresh evaluator instance (D3), exactly like the reconciliation
/// pass.</summary>
public sealed class AlertAutoResolveService(
    IServiceScopeFactory scopes,
    ILogger<AlertAutoResolveService> log) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAlertEvaluator>()
                    .AutoResolveDueAsync(DateTimeOffset.UtcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Alert auto-resolve tick failed");
            }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>Alert reconciliation (API.md §9.3): repairs state after restart.</summary>
public sealed class AlertReconciliationService(
    IServiceScopeFactory scopes,
    ILogger<AlertReconciliationService> log) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAlertEvaluator>().ReconcileAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Alert reconciliation failed");
            }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>Notification outbox dispatcher (API.md §9.4): durable, retryable
/// delivery; a provider failure never rolls back the committed alert.</summary>
public sealed class NotificationDispatchService(
    IServiceScopeFactory scopes,
    ILogger<NotificationDispatchService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
                var alerts = scope.ServiceProvider.GetRequiredService<IAlertStore>();
                var hosts = scope.ServiceProvider.GetRequiredService<IHostStore>();
                var now = DateTimeOffset.UtcNow;

                foreach (var item in await outbox.DequeueDueAsync(max: 20, now, ct))
                {
                    try
                    {
                        var alert = item.AlertId is null ? null : await alerts.GetAsync(item.AlertId, ct);
                        var hostName = alert?.HostId is null ? null : (await hosts.GetAsync(alert.HostId, ct))?.Name;
                        var message = new NotificationMessage(
                            alert?.Title ?? "Hyveman notification",
                            alert?.Detail ?? "",
                            alert?.Severity ?? "info",
                            null,
                            hostName);
                        var result = await sender.SendToChannelAsync(item.ChannelId, message, ct);
                        await outbox.MarkResultAsync(item.Id, result.Ok, result.Error, DateTimeOffset.UtcNow, ct);
                    }
                    catch (Exception ex)
                    {
                        await outbox.MarkResultAsync(item.Id, false, ex.Message, DateTimeOffset.UtcNow, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Notification dispatch tick failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>Retention/backup/cleanup maintenance (API.md §9.5): hourly
/// retention + cleanup; daily VACUUM INTO snapshots with the 7/4/12 ladder.</summary>
public sealed class MaintenanceService(
    IServiceScopeFactory scopes,
    ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastBackupDay = -1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var job = scope.ServiceProvider.GetRequiredService<IMaintenanceJob>();
                var now = DateTimeOffset.UtcNow;
                await job.RunRetentionAsync(now, ct);
                await job.RunCleanupAsync(now, ct);
                if (now.Day != lastBackupDay)
                {
                    await job.RunBackupAsync(now, ct);
                    lastBackupDay = now.Day;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Maintenance tick failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(60), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
