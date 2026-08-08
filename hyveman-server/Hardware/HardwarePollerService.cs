using System.Collections.Concurrent;
using Hyveman.Server.Auth;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;
using Microsoft.Extensions.Options;

namespace Hyveman.Server.Hardware;

/// <summary>
/// Per-host poll loop (§8.3): independent per-host timing, bounded concurrency, one host's
/// slow/down iDRAC never delays others. Failure → components 'unknown' (never deleted) +
/// a host_unreachable signal to the alert engine.
/// </summary>
public sealed class HardwarePollerService : BackgroundService
{
    private readonly Db _db;
    private readonly ServerOptions _opts;
    private readonly IHardwareProvider _provider;
    private readonly ICredentialVault _vault;
    private readonly Alerts.IHostUnreachableSignal _unreachableSignal;
    private readonly Observability.OwnMetrics _metrics;
    private readonly ILogger<HardwarePollerService> _logger;

    /// <summary>Consecutive failed polls per host (for the N-consecutive unreachable rule, §9.4).</summary>
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();

    public HardwarePollerService(Db db, ServerOptions opts, IHardwareProvider provider, ICredentialVault vault,
        Alerts.IHostUnreachableSignal unreachableSignal, Observability.OwnMetrics metrics, ILogger<HardwarePollerService> logger)
    {
        _db = db;
        _opts = opts;
        _provider = provider;
        _vault = vault;
        _unreachableSignal = unreachableSignal;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opts.Poller.IntervalS));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hardware poll cycle failed");
            }
        }
    }

    public async Task PollAllAsync(CancellationToken ct)
    {
        var hosts = await _db.Hosts.ListAsync(pollEnabledOnly: true);
        if (hosts.Count == 0) return;

        var sem = new SemaphoreSlim(_opts.Poller.Concurrency);
        var tasks = hosts.Where(h => !string.IsNullOrEmpty(h.IdracUrl)).Select(async host =>
        {
            await sem.WaitAsync(ct);
            try
            {
                await PollOneAsync(host, ct);
            }
            finally
            {
                sem.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task PollOneAsync(HostRow host, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (user, pass) = await ResolveCredsAsync(host);
            var health = await _provider.PollAsync(host, user, pass, ct);
            await ComponentNormalizer.StoreAsync(_db, host, health, WireTime.NowMs());

            _consecutiveFailures.TryRemove(host.Id, out _);
            _metrics.PollSuccess(host.Id, sw.ElapsedMilliseconds);
            _unreachableSignal.OnPollSuccess(host.Id);
            _logger.LogDebug("Polled {Host}: {State} ({Components} components)", host.Name, health.RollupState, health.Components.Count);
        }
        catch (Exception ex)
        {
            var failures = _consecutiveFailures.AddOrUpdate(host.Id, 1, (_, c) => c + 1);
            await ComponentNormalizer.MarkUnreachableAsync(_db, host, WireTime.NowMs());
            _metrics.PollFailure(host.Id);
            _logger.LogWarning("Poll of {Host} failed ({Failure} consecutive): {Error}", host.Name, failures, ex.Message);

            if (failures >= _opts.Alerts.IdracUnreachablePolls)
                _unreachableSignal.OnHostUnreachable(host.Id, failures, ex.Message);
        }
    }

    private async Task<(string user, string pass)> ResolveCredsAsync(HostRow host)
    {
        if (string.IsNullOrEmpty(host.IdracCredRef))
            throw new InvalidOperationException($"host {host.Name} has no idrac_cred_ref configured");
        var secret = await _vault.GetSecretAsync(host.IdracCredRef)
            ?? throw new InvalidOperationException($"credential '{host.IdracCredRef}' not found in vault");
        var parts = secret.Split('\n', 2);
        if (parts.Length != 2)
            throw new InvalidOperationException($"credential '{host.IdracCredRef}' must be 'username\\npassword'");
        return (parts[0].Trim(), parts[1].Trim());
    }
}
