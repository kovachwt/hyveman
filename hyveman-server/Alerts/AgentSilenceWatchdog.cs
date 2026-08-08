using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;

namespace Hyveman.Server.Alerts;

/// <summary>
/// 30s sweep detecting missed agent heartbeats vs per-rule threshold (§9.4, DESIGN §4.4 type 3).
/// iDRAC-unreachable handling arrives via poller signals; this watchdog covers agent silence
/// and clears both when state recovers.
/// </summary>
public sealed class AgentSilenceWatchdog : BackgroundService
{
    private readonly Db _db;
    private readonly ServerOptions _opts;
    private readonly AlertEngineService _engine;
    private readonly ILogger<AgentSilenceWatchdog> _logger;

    public AgentSilenceWatchdog(Db db, ServerOptions opts, AlertEngineService engine, ILogger<AgentSilenceWatchdog> logger)
    {
        _db = db;
        _opts = opts;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent silence sweep failed");
            }
        }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        // Engine's heartbeat-rule evaluation covers silence; this watchdog additionally
        // ensures alerts exist for sources with NO heartbeat row at all (registered but silent).
        var rules = (await _db.Alerts.ListRulesAsync(enabledOnly: true)).Where(r => r.Type == "heartbeat").ToList();
        var agentRules = rules.Where(r => !AlertEngineService.ParseMatch(r.MatchJson).Unreachable).ToList();
        if (agentRules.Count == 0) return;

        var sources = await _db.Sources.ListAsync();
        var heartbeats = await _db.Heartbeats.AllAsync();
        var hbBySource = heartbeats.ToDictionary(h => h.SourceId);
        var now = DateTimeOffset.UtcNow;

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source.Kind is not ("windows-agent" or "linux-agent")) continue;
            var host = await _db.Hosts.GetBySourceIdAsync(source.Id);
            var hb = hbBySource.GetValueOrDefault(source.Id);
            foreach (var rule in agentRules)
            {
                var match = AlertEngineService.ParseMatch(rule.MatchJson);
                var missS = match.MissS > 0 ? match.MissS : _opts.Alerts.DefaultHeartbeatMissS;
                var silent = hb is null
                    || !WireTime.TryParseUtc(hb.ReceivedAt, out var received)
                    || (now - received).TotalSeconds > missS;
                var signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"heartbeat:{source.Id}"))).ToLowerInvariant()[..32];
                if (silent)
                {
                    if (hb is null)
                    {
                        await _engine.FireAsync(rule, host?.Id, source.Id, signature, rule.Severity,
                            $"{{\"never_heartbeat\":true,\"registered_at\":\"{source.Created}\"}}", ct);
                    }
                    else if (WireTime.TryParseUtc(hb.ReceivedAt, out var received2))
                    {
                        await _engine.FireAsync(rule, host?.Id, source.Id, signature, rule.Severity,
                            $"{{\"missed_for_s\":{(long)(now - received2).TotalSeconds},\"received_at\":\"{hb.ReceivedAt}\"}}", ct);
                    }
                }
                else
                {
                    await _engine.ResolveAsync(rule, host?.Id, source.Id, signature, ct);
                }
            }
        }
    }
}

/// <summary>Filters alerts for hosts inside an active maintenance window (§9.3).</summary>
public sealed class MaintenanceWindowFilter
{
    private readonly Db _db;

    public MaintenanceWindowFilter(Db db) => _db = db;

    public async Task<bool> IsInWindowAsync(string hostId, DateTimeOffset now)
    {
        var windows = await _db.Alerts.WindowsForHostAsync(hostId, activeOnly: true);
        foreach (var w in windows)
        {
            if (!Common.WireTime.TryParseUtc(w.Start, out var start)) continue;
            if (!Common.WireTime.TryParseUtc(w.End, out var end)) continue;
            if (now >= start && now <= end) return true;
        }
        return false;
    }
}
