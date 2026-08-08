using System.Collections.Concurrent;
using System.Text.Json;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;
using Microsoft.Data.Sqlite;
using AlertRepo = Hyveman.Server.Storage.Repos.AlertRepository;

namespace Hyveman.Server.Alerts;

/// <summary>Signals from producers into the alert engine (event-driven evaluation, §9.1).</summary>
public interface IEventSignal { void OnNewEvents(string sourceId, int count); }
public interface IHeartbeatSignal { void OnHeartbeat(string sourceId); }
public interface IHostUnreachableSignal
{
    void OnHostUnreachable(string hostId, int consecutiveFailures, string error);
    void OnPollSuccess(string hostId);
}

public sealed class AlertSignals : IEventSignal, IHeartbeatSignal, IHostUnreachableSignal
{
    private readonly AlertEngineService _engine;
    public AlertSignals(AlertEngineService engine) => _engine = engine;
    public void OnNewEvents(string sourceId, int count) => _engine.Signal(AlertEngineService.SignalKind.NewEvents, sourceId, null, null, null);
    public void OnHeartbeat(string sourceId) => _engine.Signal(AlertEngineService.SignalKind.Heartbeat, sourceId, null, null, null);
    public void OnHostUnreachable(string hostId, int failures, string error)
        => _engine.Signal(AlertEngineService.SignalKind.HostUnreachable, null, hostId, failures, error);
    public void OnPollSuccess(string hostId)
        => _engine.Signal(AlertEngineService.SignalKind.PollSuccess, null, hostId, null, null);
}

/// <summary>
/// Health + heartbeat rule evaluation with dedup/bump/cooldown/escalation (§9.1–§9.2).
/// Sweep every alerts.sweep_s; signals trigger immediate re-evaluation.
/// </summary>
public sealed class AlertEngineService : BackgroundService, IDisposable
{
    public enum SignalKind { NewEvents, Heartbeat, HostUnreachable, PollSuccess }

    private readonly Db _db;
    private readonly ServerOptions _opts;
    private readonly Notifications.NotificationDispatcher _dispatcher;
    private readonly MaintenanceWindowFilter _maintenance;
    private readonly Observability.OwnMetrics _metrics;
    private readonly ILogger<AlertEngineService> _logger;

    private readonly ConcurrentQueue<(SignalKind kind, string? sourceId, string? hostId, int? failures, string? error)> _signals = new();
    private readonly SemaphoreSlim _evalGate = new(1, 1);

    /// <summary>Last known component state per host: (type,name) → state. Baseline on startup; diff on sweep.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(string Type, string Name), string>> _lastStates = new();
    private bool _baselineLoaded;

    public AlertEngineService(Db db, ServerOptions opts, Notifications.NotificationDispatcher dispatcher,
        MaintenanceWindowFilter maintenance, Observability.OwnMetrics metrics, ILogger<AlertEngineService> logger)
    {
        _db = db;
        _opts = opts;
        _dispatcher = dispatcher;
        _maintenance = maintenance;
        _metrics = metrics;
        _logger = logger;
    }

    public void Signal(SignalKind kind, string? sourceId, string? hostId, int? failures, string? error)
        => _signals.Enqueue((kind, sourceId, hostId, failures, error));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedDefaultRulesAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opts.Alerts.SweepS));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert engine sweep failed");
            }
        }
    }

    private async Task SeedDefaultRulesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rules = await _db.Alerts.ListRulesAsync();
        if (rules.Count > 0) return;
        var now = WireTime.Now();
        await _db.Writer.WithTransactionAsync(async conn =>
        {
            await _db.Alerts.InsertRuleAsync(conn, Ulid.Prefixed("rule_"), "Hardware component degraded",
                "health", "{\"states\":[\"warning\",\"critical\"]}", "warning", 300);
            await _db.Alerts.InsertRuleAsync(conn, Ulid.Prefixed("rule_"), "Agent silent",
                "heartbeat", "{}", "critical", 600);
            await _db.Alerts.InsertRuleAsync(conn, Ulid.Prefixed("rule_"), "iDRAC unreachable",
                "heartbeat", "{\"unreachable\":true}", "warning", 900);
        });
        _logger.LogInformation("Seeded default alert rules (health + heartbeat)");
    }

    public async Task EvaluateAsync(CancellationToken ct)
    {
        if (!await _evalGate.WaitAsync(0, ct)) return;
        try
        {
            await LoadBaselineAsync(ct);
            await EvaluateHealthRulesAsync(ct);
            await EvaluateHeartbeatRulesAsync(ct);
            await ProcessSignalsAsync(ct);
        }
        finally
        {
            _evalGate.Release();
        }
    }

    /// <summary>First sweep only: snapshot current states as the no-fire baseline (§9.1).</summary>
    private async Task LoadBaselineAsync(CancellationToken ct)
    {
        if (_baselineLoaded) return;
        ct.ThrowIfCancellationRequested();
        var hosts = await _db.Hosts.ListAsync();
        foreach (var host in hosts)
        {
            var states = await _db.Components.CurrentStatesAsync(host.Id);
            _lastStates[host.Id] = new ConcurrentDictionary<(string, string), string>(
                states.ToDictionary(s => (s.Type, s.Name), s => s.State));
        }
        _baselineLoaded = true;
    }

    private async Task EvaluateHealthRulesAsync(CancellationToken ct)
    {
        var rules = (await _db.Alerts.ListRulesAsync(enabledOnly: true)).Where(r => r.Type == "health").ToList();
        if (rules.Count == 0) return;

        var hosts = await _db.Hosts.ListAsync();
        foreach (var host in hosts)
        {
            ct.ThrowIfCancellationRequested();
            var current = await _db.Components.CurrentStatesAsync(host.Id);
            var prev = _lastStates.GetOrAdd(host.Id, _ => new ConcurrentDictionary<(string, string), string>());
            foreach (var (type, name, state) in current)
            {
                var key = (type, name);
                var old = prev.TryGetValue(key, out var o) ? o : null;
                if (old == state) continue;
                prev[key] = state;

                foreach (var rule in rules)
                {
                    var match = ParseMatch(rule.MatchJson);
                    if (match.Types.Count > 0 && !match.Types.Contains(type)) continue;
                    var signature = Signature("health", $"{type}:{name}");
                    if (IsBad(state) && IsBad(old))
                    {
                        // warning→critical escalation
                        if (SeverityRank(state) > SeverityRank(old!))
                            await FireAsync(rule, host.Id, null, signature, Escalate(rule.Severity, state), $"{{\"component\":\"{type}:{name}\",\"state\":\"{state}\"}}", ct);
                        continue;
                    }
                    if (IsBad(state) && !IsBad(old))
                    {
                        await FireAsync(rule, host.Id, null, signature, rule.Severity, $"{{\"component\":\"{type}:{name}\",\"state\":\"{state}\"}}", ct);
                    }
                    else if (!IsBad(state) && IsBad(old))
                    {
                        await ResolveAsync(rule, host.Id, null, signature, ct);
                    }
                }
            }
            // Components that disappeared (deleted rows) → drop from memory.
            foreach (var k in prev.Keys)
                if (!current.Any(c => c.Type == k.Item1 && c.Name == k.Item2))
                    prev.TryRemove(k, out _);
        }
    }

    private async Task EvaluateHeartbeatRulesAsync(CancellationToken ct)
    {
        var rules = (await _db.Alerts.ListRulesAsync(enabledOnly: true)).Where(r => r.Type == "heartbeat").ToList();
        if (rules.Count == 0) return;

        var heartbeats = await _db.Heartbeats.AllAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var rule in rules)
        {
            var match = ParseMatch(rule.MatchJson);
            if (match.Unreachable) continue;   // handled by the watchdog path (signals)
            var missS = match.MissS > 0 ? match.MissS : _opts.Alerts.DefaultHeartbeatMissS;

            foreach (var hb in heartbeats)
            {
                if (!WireTime.TryParseUtc(hb.ReceivedAt, out var received)) continue;
                var host = await _db.Hosts.GetBySourceIdAsync(hb.SourceId);
                var isSilent = (now - received).TotalSeconds > missS;
                if (isSilent)
                {
                    await FireAsync(rule, host?.Id, hb.SourceId, Signature("heartbeat", hb.SourceId), rule.Severity,
                        $"{{\"missed_for_s\":{(long)(now - received).TotalSeconds},\"received_at\":\"{hb.ReceivedAt}\"}}", ct);
                }
                else
                {
                    await ResolveAsync(rule, host?.Id, hb.SourceId, Signature("heartbeat", hb.SourceId), ct);
                }
            }
        }
    }

    private async Task ProcessSignalsAsync(CancellationToken ct)
    {
        while (_signals.TryDequeue(out var sig))
        {
            switch (sig.kind)
            {
                case SignalKind.Heartbeat:
                case SignalKind.NewEvents:
                    break;   // Phase 1: heartbeat freshness handled by sweep; event rules are Phase 2 (no-op).
                case SignalKind.HostUnreachable:
                    await HandleHostUnreachableAsync(sig, ct);
                    break;
                case SignalKind.PollSuccess:
                    // Resolve unreachable alerts for the host.
                    foreach (var rule in (await _db.Alerts.ListRulesAsync(enabledOnly: true)).Where(r => r.Type == "heartbeat"))
                    {
                        var match = ParseMatch(rule.MatchJson);
                        if (match.Unreachable && sig.hostId is not null)
                            await ResolveAsync(rule, sig.hostId, null, Signature("unreachable", sig.hostId), ct);
                    }
                    break;
            }
        }
    }

    private async Task HandleHostUnreachableAsync((SignalKind kind, string? sourceId, string? hostId, int? failures, string? error) sig, CancellationToken ct)
    {
        var host = sig.hostId is null ? null : await _db.Hosts.GetAsync(sig.hostId);
        foreach (var rule in (await _db.Alerts.ListRulesAsync(enabledOnly: true)).Where(r => r.Type == "heartbeat"))
        {
            var match = ParseMatch(rule.MatchJson);
            if (!match.Unreachable) continue;
            await FireAsync(rule, host?.Id, null, Signature("unreachable", sig.hostId ?? ""), rule.Severity,
                $"{{\"consecutive_failures\":{sig.failures ?? 1},\"error\":\"{(sig.error ?? "").Replace("\"", "'")}\"}}", ct);
        }
    }

    // ── fire / resolve ─────────────────────────────────────────────────────
    public async Task FireAsync(RuleRow rule, string? hostId, string? sourceId, string signature,
        string severity, string detailJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = WireTime.NowMs();
        var nowDt = DateTimeOffset.UtcNow;

        // Maintenance window: suppressed (status 'silenced'), recorded for history, no notifications (§9.3).
        var inMaintenance = hostId is not null && await _maintenance.IsInWindowAsync(hostId, nowDt);

        var outcome = await _db.Writer.WithTransactionAsync(async conn =>
        {
            var existing = await _db.Alerts.FindActiveAsync(rule.Id, hostId, sourceId, signature);
            if (existing is not null)
            {
                await AlertRepo.BumpAsync(conn, existing.Id, now);
                // Persist escalation (severity bump, e.g. warning→critical) and fresh detail (§9.2).
                if (existing.Severity != severity || existing.DetailJson != detailJson)
                    await AlertRepo.UpdateSeverityDetailAsync(conn, existing.Id, severity, detailJson);
                return (alert: existing with { Severity = severity, DetailJson = detailJson }, created: false);
            }
            var id = Ulid.Prefixed("alert_");
            var (_, created) = await AlertRepo.UpsertAsync(conn, id, rule.Id, hostId, sourceId,
                severity, signature, now, detailJson);
            return (alert: new AlertRow(id, rule.Id, hostId, sourceId, severity, signature, now, now, 1, "active", detailJson, null), created: created);
        });

        _metrics.AlertFired();
        var alert = outcome.alert;
        if (outcome.created && inMaintenance)
        {
            await _db.Writer.WithTransactionAsync(conn => AlertRepo.SetStatusAsync(conn, alert.Id, "silenced", now));
        }
        if (!inMaintenance)
        {
            await NotifyIfCooldownElapsedAsync(rule, alert, nowDt, ct);
        }
    }

    public async Task ResolveAsync(RuleRow rule, string? hostId, string? sourceId, string signature, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var existing = await _db.Alerts.FindActiveAsync(rule.Id, hostId, sourceId, signature);
        if (existing is null) return;
        await _db.Writer.WithTransactionAsync(conn => AlertRepo.ResolveAsync(conn, existing.Id, WireTime.NowMs()));
        _metrics.AlertResolved();
    }

    private async Task NotifyIfCooldownElapsedAsync(RuleRow rule, AlertRow alert, DateTimeOffset now, CancellationToken ct)
    {
        // Cooldown applies between notifications for the same dedup key (§9.2); bumps still happen.
        // last_notified_at is persisted so a restart cannot reset the cooldown and re-notify.
        var channels = await _db.Alerts.ChannelsForRuleAsync(alert.RuleId);
        if (channels.Count == 0) return;   // nothing to notify → don't start the cooldown clock

        var cooldown = TimeSpan.FromSeconds(Math.Max(0, rule.Cooldown));
        // Atomically claim the notification slot: re-read the persisted timestamp inside the
        // write transaction and stamp it, so concurrent fires cannot double-notify.
        var claimed = await _db.Writer.WithTransactionAsync(async conn =>
        {
            var fresh = await _db.Alerts.GetAsync(alert.Id);
            if (fresh?.LastNotifiedAt is not null
                && WireTime.TryParseUtc(fresh.LastNotifiedAt, out var lastNotified)
                && now - lastNotified < cooldown)
                return false;
            await AlertRepo.MarkNotifiedAsync(conn, alert.Id, WireTime.NowMs());
            return true;
        });
        if (!claimed) return;
        await _dispatcher.EnqueueAsync(alert, ct);
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static string Signature(string kind, string identity)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}:{identity}"))).ToLowerInvariant()[..32];

    private static bool IsBad(string? state) => state is "warning" or "critical";

    private static int SeverityRank(string state) => state switch { "critical" => 2, "warning" => 1, _ => 0 };

    private static string Escalate(string ruleSeverity, string state)
        => state == "critical" && ruleSeverity != "critical" ? "critical" : ruleSeverity;

    public static RuleMatch ParseMatch(string matchJson)
    {
        var m = new RuleMatch();
        try
        {
            using var doc = JsonDocument.Parse(matchJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("miss_s", out var miss) && miss.TryGetInt32(out var ms)) m.MissS = ms;
            if (root.TryGetProperty("unreachable", out var un) && un.ValueKind == System.Text.Json.JsonValueKind.True) m.Unreachable = true;
            if (root.TryGetProperty("types", out var types) && types.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var t in types.EnumerateArray()) m.Types.Add(t.GetString() ?? "");
            if (root.TryGetProperty("states", out var states) && states.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var s in states.EnumerateArray()) m.States.Add(s.GetString() ?? "");
        }
        catch (JsonException) { }
        return m;
    }

    public sealed class RuleMatch
    {
        public int MissS { get; set; }
        public bool Unreachable { get; set; }
        public List<string> Types { get; set; } = new();
        public List<string> States { get; set; } = new();
    }

    public override void Dispose()
    {
        _evalGate.Dispose();
        base.Dispose();
    }
}
