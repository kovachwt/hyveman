using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hyveman.Domain;
using Microsoft.Extensions.Logging;

namespace Hyveman.Application;

/// <summary>
/// Alert rule engine (API.md §9.3, DESIGN §4.4): health-state transitions,
/// event matches, heartbeat silence, threshold crossings, deduplication,
/// cooldown, maintenance suppression and resolution. The uniqueness model is
/// (rule_id, host_id, fingerprint, live state): a resolved occurrence is
/// followed by a new occurrence without losing history.
///
/// The evaluator is stateless by design (DEFECTS.md D3): every transition
/// input — previous component/rollup state, previous threshold crossing,
/// last occurrence for cooldown — is read from the durable stores, never from
/// instance fields. It is therefore safe to construct fresh per request/scope;
/// a restart loses nothing that the 6-hourly reconciliation pass cannot repair.
/// </summary>
public sealed class AlertEvaluatorService(
    IRuleStore rules,
    IAlertStore alerts,
    IHostStore hosts,
    ISourceStore sources,
    IHealthStore health,
    IAgentStatusStore agentStatus,
    IMaintenanceWindowStore windows,
    IOutboxStore outbox,
    IClock clock,
    ILogger<AlertEvaluatorService> log) : IAlertEvaluator
{
    public async Task OnEventsAcceptedAsync(string sourceId, IReadOnlyList<ValidatedLogItem> items, CancellationToken ct)
    {
        var source = await sources.GetByIdAsync(sourceId, ct);
        if (source is null) return;
        var host = await hosts.GetBySourceAsync(sourceId, ct);
        var ruleList = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.Event).ToList();
        if (ruleList.Count == 0) return;

        var at = clock.UtcNow;
        foreach (var rule in ruleList)
        {
            var match = RuleMatch.ParseEvent(rule.MatchJson);
            if (match is null || !MatchSourceKind(match, source.Kind)) continue;
            foreach (var item in items)
            {
                if (!MatchEvent(match, item)) continue;
                var fingerprint = $"event:{item.Channel ?? ""}:{item.EventId?.ToString() ?? ""}";
                var title = item.EventId is { } eid
                    ? $"Event {eid} on {item.Channel ?? "unknown channel"}"
                    : $"Event on {item.Channel ?? "unknown channel"}";
                await FireAsync(rule, host?.Id, sourceId, fingerprint, title,
                    Truncate(item.Message, 200), at, bump: true, ct);
            }
        }
    }

    /// <summary>Logon rules (DESIGN §4.4 type 6): fires when a Security logon
    /// event matches the rule's outcome (success/failure/lockout), user list
    /// (empty = any user) and optional logon-type list. The classification is
    /// shared with `logon_stats` (LogonStatsService.TryClassify) so the two
    /// derived consumers can never disagree about what a logon event is.
    /// DWM-x/UMFD-x internal console-session accounts (4624 noise on hosts)
    /// are ignored for any-user rules; a rule that explicitly lists one still
    /// matches. Event-style semantics: fire and bump per occurrence with the
    /// rule's cooldown; no resolution phase.</summary>
    public async Task OnLogonEventsAsync(string sourceId, IReadOnlyList<ValidatedLogItem> items, CancellationToken ct)
    {
        var ruleList = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.Logon).ToList();
        if (ruleList.Count == 0) return;
        var source = await sources.GetByIdAsync(sourceId, ct);
        if (source is null) return;
        var host = await hosts.GetBySourceAsync(sourceId, ct);

        var at = clock.UtcNow;
        foreach (var rule in ruleList)
        {
            var match = RuleMatch.ParseLogon(rule.MatchJson);
            if (match is null || !MatchSourceKind(match, source.Kind)) continue;
            var anyUser = match.Users.Count == 0;
            foreach (var item in items)
            {
                var info = LogonStatsService.TryClassify(item);
                if (info is null || info.Outcome != match.Outcome) continue;
                // Internal console/desktop-session accounts are never human
                // logins; keep them out of any-user rules (an explicit user
                // list still matches, e.g. to debug session oddities).
                if (anyUser && NoiseAccount.IsMatch(info.User)) continue;
                if (!anyUser && !match.Users.Any(u => string.Equals(u, info.User, StringComparison.OrdinalIgnoreCase))) continue;
                if (match.LogonTypes.Count > 0 && (info.LogonType is null || !match.LogonTypes.Contains(info.LogonType.Value))) continue;

                var user = info.User;
                // Windows account names are case-insensitive; a stable
                // fingerprint avoids per-case alert churn.
                var fingerprint = $"logon:{info.Outcome}:{user.ToLowerInvariant()}";
                var title = info.Outcome switch
                {
                    LogonOutcomes.Success => $"Successful logon: {user}",
                    LogonOutcomes.Failure => $"Failed logon: {user}",
                    _ => $"Account lockout: {user}",
                };
                var detail = info.LogonType is { } lt
                    ? $"{user} on {source.Name} (logon type {lt})"
                    : $"{user} on {source.Name}";
                await FireAsync(rule, host?.Id, sourceId, fingerprint, title, detail, at, bump: true, ct);
            }
        }
    }

    public async Task OnHealthStateChangedAsync(string hostId, string rollupState,
        IReadOnlyList<ComponentRecord> components, DateTimeOffset at, CancellationToken ct)
    {
        var ruleList = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.Health).ToList();
        if (ruleList.Count == 0) return;

        // D3: the previous poll's state is read from the store — the caller
        // (HardwarePollingService) evaluates BEFORE ReplaceComponentsAsync — so
        // transition detection is durable and scope/instance-independent.
        var prevComponents = await health.GetComponentsAsync(hostId, ct);
        var prevRollup = RollupOf(prevComponents);

        // Rollup-level evaluation.
        if (prevRollup != rollupState)
        {
            await EvaluateRollupForRulesAsync(ruleList, hostId, prevRollup, rollupState, at, ct);
        }

        // Component-level evaluation (delta against the stored previous poll).
        var prevByKey = prevComponents
            .GroupBy(c => $"{c.Type}|{c.Name}")
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var comp in components)
        {
            var ckey = $"{comp.Type}|{comp.Name}";
            var wire = HealthStates.ToWire(comp.State);
            var prev = prevByKey.TryGetValue(ckey, out var prevComp)
                ? HealthStates.ToWire(prevComp.State)
                : HealthStates.ToWire(HealthState.Unknown);
            if (prev == wire) continue;

            foreach (var rule in ruleList)
            {
                var match = RuleMatch.ParseHealth(rule.MatchJson);
                if (match is null) continue;
                if (match.ComponentTypes.Count > 0 && !match.ComponentTypes.Contains(comp.Type)) continue;

                if (match.States.Contains(wire))
                {
                    await FireAsync(rule, hostId, null, $"component:{comp.Type}:{comp.Name}:{wire}",
                        $"{comp.Name} is {wire} ({comp.Type})", comp.Detail, at, bump: true, ct);
                }
                else if (prev is "warning" or "critical")
                {
                    await ResolveAsync(rule, hostId, null, $"component:{comp.Type}:{comp.Name}:{prev}", at, ct);
                }
            }
        }
    }

    private async Task EvaluateRollupForRulesAsync(IReadOnlyList<RuleRecord> ruleList, string hostId,
        string prevRollup, string newRollup, DateTimeOffset at, CancellationToken ct)
    {
        foreach (var rule in ruleList)
        {
            var match = RuleMatch.ParseHealth(rule.MatchJson);
            if (match is null || !match.IncludeRollup) continue;
            if (match.States.Contains(newRollup))
            {
                await FireAsync(rule, hostId, null, $"rollup:{newRollup}",
                    $"Hardware rollup is {newRollup}", $"Host {hostId} overall health: {newRollup}", at, bump: true, ct);
            }
            else if (prevRollup is "warning" or "critical")
            {
                await ResolveAsync(rule, hostId, null, $"rollup:{prevRollup}", at, ct);
            }
        }
    }

    public async Task OnHeartbeatSilenceChangedAsync(string? ruleId, string sourceId, bool silent,
        DateTimeOffset at, CancellationToken ct)
    {
        var source = await sources.GetByIdAsync(sourceId, ct);
        if (source is null) return;
        var host = await hosts.GetBySourceAsync(sourceId, ct);

        if (silent)
        {
            if (ruleId is null) return;
            var rule = await rules.GetAsync(ruleId, ct);
            if (rule is null || !rule.Enabled || rule.Type != RuleTypes.Heartbeat) return;
            await FireAsync(rule, host?.Id, sourceId, "heartbeat:silent",
                $"Agent silent: {source.Name}", $"No heartbeat from {source.Name} for the configured threshold", at, bump: true, ct);
        }
        else
        {
            // Heartbeat arrived: clear silence for every heartbeat rule.
            var hbRules = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.Heartbeat).ToList();
            foreach (var rule in hbRules)
                await ResolveAsync(rule, host?.Id, sourceId, "heartbeat:silent", at, ct);
        }
    }

    /// <summary>VM heartbeat transitions (DESIGN §4.4 rule type 5): fires when a
    /// running VM whose stored heartbeat was OK reports a lost heartbeat, and
    /// resolves when the heartbeat returns or the VM leaves the running state.
    /// D3: the caller (TelemetryService) evaluates BEFORE the latest-wins
    /// upsert, so the previous facts are read from the durable store here.</summary>
    public async Task OnVmsChangedAsync(string hostId, IReadOnlyList<VmRecord> vms,
        DateTimeOffset at, CancellationToken ct)
    {
        var ruleList = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.VmHeartbeat).ToList();
        if (ruleList.Count == 0) return;

        var prevVms = await health.GetVmsAsync(hostId, ct);
        var prevByName = prevVms.ToDictionary(v => v.Name);
        var parsed = ruleList
            .Select(r => (Rule: r, Match: RuleMatch.ParseVmHeartbeat(r.MatchJson)))
            .Where(x => x.Match is not null && MatchSourceKind(x.Match!, "windows-agent"))
            .ToList();
        if (parsed.Count == 0) return;

        foreach (var vm in vms)
        {
            var prev = prevByName.GetValueOrDefault(vm.Name);
            // "Had an OK heartbeat, suddenly lost it": a fresh OK→lost
            // transition while the VM is running. Powered-off/saved/paused
            // VMs report heartbeat_ok=null by design (the agent only
            // heartbeats running VMs, HYPERV §4.4), so a loss is only
            // meaningful while state is on; and a prev that is already lost
            // is skipped so count stays one per episode, not one per scan.
            if (prev is null || prev.HeartbeatOk != true || vm.HeartbeatOk == true || vm.State != "on")
                continue;

            foreach (var (rule, _) in parsed)
            {
                await FireAsync(rule, hostId, null, $"vmheartbeat:{vm.Name}",
                    $"VM lost heartbeat: {vm.Name}",
                    $"VM {vm.Name} heartbeat was OK and is now lost (state: {vm.State})", at, bump: true, ct);
            }
        }

        // Resolution: the heartbeat returned, or the VM left the running state
        // (a graceful power-off makes the loss moot). No-op when no live alert.
        foreach (var vm in vms)
        {
            if (vm.HeartbeatOk != true && vm.State == "on") continue;
            foreach (var (rule, _) in parsed)
                await ResolveAsync(rule, hostId, null, $"vmheartbeat:{vm.Name}", at, ct);
        }
    }

    public async Task OnThresholdsAsync(string hostId, IReadOnlyList<MetricRecord> metrics,
        DateTimeOffset at, CancellationToken ct)
    {
        var ruleList = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.Threshold).ToList();
        if (ruleList.Count == 0) return;

        foreach (var rule in ruleList)
        {
            var match = RuleMatch.ParseThreshold(rule.MatchJson);
            if (match is null) continue;
            foreach (var metric in metrics)
            {
                if (!string.Equals(metric.Name, match.Metric, StringComparison.OrdinalIgnoreCase)) continue;
                var crossing = match.Comparator switch
                {
                    "gt" => metric.Value > match.Value,
                    "gte" => metric.Value >= match.Value,
                    "lt" => metric.Value < match.Value,
                    "lte" => metric.Value <= match.Value,
                    "eq" => Math.Abs(metric.Value - match.Value) < 0.0001,
                    _ => false,
                };
                // D3: the previous crossing state is the live alert itself —
                // no in-memory history. Fire only on a fresh crossing (no live
                // alert), resolve on a recovered crossing (live alert present).
                var key = AlertKey(rule, hostId, null, $"threshold:{metric.Name}");
                var live = await alerts.FindLiveAsync(key, ct);
                if (crossing && live is null)
                {
                    await FireAsync(rule, hostId, null, $"threshold:{metric.Name}",
                        $"{metric.Name} {match.Comparator} {match.Value}", $"{metric.Name} = {metric.Value:0.##} {metric.Unit}", at, bump: true, ct);
                }
                else if (!crossing && live is not null)
                {
                    await ResolveAsync(rule, hostId, null, $"threshold:{metric.Name}", at, ct);
                }
            }
        }
    }

    /// <summary>VM replication crossings (DESIGN §4.4 rule type 7): fires
    /// when a VM's replication health/state enters the rule's bad set and
    /// resolves when it no longer matches. Threshold-style (like
    /// OnThresholdsAsync): the live alert itself is the crossing state (D3),
    /// so a fresh evaluator enforces it identically and restarts lose
    /// nothing. Non-replicated VMs report null fields — never a match.
    /// stale:true facts are filtered by the caller (TelemetryService).</summary>
    public async Task OnVmReplicationChangedAsync(string hostId, IReadOnlyList<VmRecord> vms,
        DateTimeOffset at, CancellationToken ct)
    {
        var ruleList = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.VmReplication).ToList();
        if (ruleList.Count == 0) return;

        var parsed = ruleList
            .Select(r => (Rule: r, Match: RuleMatch.ParseVmReplication(r.MatchJson)))
            .Where(x => x.Match is not null && MatchSourceKind(x.Match!, "windows-agent"))
            .ToList();
        if (parsed.Count == 0) return;

        foreach (var vm in vms)
        {
            foreach (var (rule, match) in parsed)
            {
                var crossing = match.Matches(vm);
                var key = AlertKey(rule, hostId, null, $"vmreplication:{vm.Name}");
                var live = await alerts.FindLiveAsync(key, ct);
                if (crossing && live is null)
                {
                    var detail = $"VM {vm.Name} replication health: {vm.ReplicationHealth ?? "—"}, state: {vm.ReplicationState ?? "—"}"
                        + (vm.ReplicationLastApplyTime is { } la ? $", last apply: {la:yyyy-MM-dd HH:mm:ss}Z" : "");
                    await FireAsync(rule, hostId, null, $"vmreplication:{vm.Name}",
                        $"VM replication degraded: {vm.Name}", detail, at, bump: true, ct);
                }
                else if (!crossing && live is not null)
                {
                    await ResolveAsync(rule, hostId, null, $"vmreplication:{vm.Name}", at, ct);
                }
            }
        }
    }

    /// <summary>Per-rule auto-resolve pass (API.md §9.3, DESIGN §4.4): event
    /// and logon rules are fire-and-bump by design — they have no natural
    /// resolution — so a rule can opt into a timeout: a live alert resolves
    /// once no new occurrence has arrived for AutoResolveAfterS seconds.
    /// Keyed off last_seen, not first_seen: every new occurrence (bump)
    /// restarts the timer, so an alert stays up while the condition is
    /// actually recurring and resolves once it goes quiet. Acknowledged and
    /// silenced alerts are resolved too — the timeout replaces the manual
    /// ack for transient noise. D3: stateless; reads rules and live alerts
    /// from the durable stores, so the pass is safe to run on any schedule
    /// and survives restarts.</summary>
    public async Task AutoResolveDueAsync(DateTimeOffset at, CancellationToken ct)
    {
        var timed = (await rules.ListAsync(ct))
            .Where(r => r.Enabled && r.AutoResolveAfterS is { } ar && ar >= 0)
            .ToDictionary(r => r.Id);
        if (timed.Count == 0) return;

        foreach (var alert in await alerts.ListLiveAsync(ct))
        {
            if (alert.RuleId is null || !timed.TryGetValue(alert.RuleId, out var rule)) continue;
            var window = rule.AutoResolveAfterS!.Value;
            if (window == 0 || (at - alert.LastSeen).TotalSeconds < window) continue;
            var resolved = alert with { Status = AlertStatuses.Resolved, ResolvedAt = at, UpdatedAt = at };
            await alerts.UpdateAsync(resolved, ct);
            log.LogInformation("Alert {alertId} auto-resolved after {window}s without a new occurrence: {title}",
                resolved.Id, window, resolved.Title);
        }
    }

    /// <summary>Reconciliation pass (API.md §9.3): re-evaluates current
    /// heartbeat and hardware state after restart; repairs alerts without
    /// inflating counts.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var at = clock.UtcNow;
        log.LogInformation("Alert reconciliation pass started");
        foreach (var host in await hosts.ListAsync(ct))
        {
            var components = await health.GetComponentsAsync(host.Id, ct);
            var rollup = RollupOf(components);

            var ruleList = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.Health).ToList();
            foreach (var rule in ruleList)
            {
                var match = RuleMatch.ParseHealth(rule.MatchJson);
                if (match is null) continue;
                if (match.IncludeRollup && match.States.Contains(rollup))
                    await FireAsync(rule, host.Id, null, $"rollup:{rollup}", $"Hardware rollup is {rollup}",
                        $"Host {host.Name} overall health: {rollup}", at, bump: false, ct);
                foreach (var comp in components)
                {
                    if (match.ComponentTypes.Count > 0 && !match.ComponentTypes.Contains(comp.Type)) continue;
                    var wire = HealthStates.ToWire(comp.State);
                    if (match.States.Contains(wire))
                        await FireAsync(rule, host.Id, null, $"component:{comp.Type}:{comp.Name}:{wire}",
                            $"{comp.Name} is {wire} ({comp.Type})", comp.Detail, at, bump: false, ct);
                }
            }
        }

        // Heartbeat silence repair.
        var hbRules = (await rules.ListAsync(ct)).Where(r => r.Enabled && r.Type == RuleTypes.Heartbeat).ToList();
        foreach (var status in await AgentStatusListAsync(ct))
        {
            foreach (var rule in hbRules)
            {
                var match = RuleMatch.ParseHeartbeat(rule.MatchJson);
                if (match is null) continue;
                var age = at - status.LastReceived;
                var silent = age > TimeSpan.FromSeconds(match.SilenceAfterS);
                if (silent)
                    await FireAsync(rule, null, status.SourceId, "heartbeat:silent",
                        $"Agent silent: {status.SourceId}", $"No heartbeat for {age.TotalSeconds:0}s", at, bump: false, ct);
            }
        }
        log.LogInformation("Alert reconciliation pass finished");
    }

    private async Task<IReadOnlyList<AgentStatusRow>> AgentStatusListAsync(CancellationToken ct)
        => await agentStatus.ListAllAsync(ct);

    private static string RollupOf(IReadOnlyList<ComponentRecord> components)
    {
        var state = HealthState.Unknown;
        foreach (var c in components)
            state = HealthStates.Max(state, c.State);
        return HealthStates.ToWire(state);
    }

    private async Task FireAsync(RuleRecord rule, string? hostId, string? sourceId, string fingerprint,
        string title, string? detail, DateTimeOffset at, bool bump, CancellationToken ct)
    {
        var key = AlertKey(rule, hostId, sourceId, fingerprint);
        var live = await alerts.FindLiveAsync(key, ct);
        if (live is not null)
        {
            if (!bump) return;
            live = live with
            {
                Count = live.Count + 1,
                LastSeen = at,
                UpdatedAt = at,
                Status = EffectiveStatus(live, at),
            };
            await alerts.UpdateAsync(live, ct);
            return;
        }

        // D3: cooldown is durable — keyed off the most recent occurrence's
        // last_seen (the resolved alert's last_seen once the previous
        // occurrence is closed), so a fresh evaluator enforces it identically
        // and it survives restarts.
        var last = await alerts.GetLatestAsync(key, ct);
        if (last is not null && (at - last.LastSeen).TotalSeconds < rule.CooldownS)
            return;
        if (hostId is not null && await windows.IsInWindowAsync(hostId, at, ct))
            return;

        var alert = new AlertRecord(
            Id: "al_" + RandomToken(18),
            RuleId: rule.Id,
            HostId: hostId,
            SourceId: sourceId,
            Key: key,
            Fingerprint: fingerprint,
            Severity: rule.Severity,
            Status: AlertStatuses.Active,
            Title: title,
            Detail: detail,
            FirstSeen: at,
            LastSeen: at,
            Count: 1,
            AckAt: null,
            AckReason: null,
            SilenceUntil: null,
            ResolvedAt: null,
            UpdatedAt: at);
        await alerts.CreateAsync(alert, ct);
        log.LogInformation("Alert {alertId} fired: {title} (severity {severity})", alert.Id, title, rule.Severity);

        var channelIds = await rules.GetChannelIdsAsync(rule.Id, ct);
        foreach (var cid in channelIds)
            await outbox.EnqueueAsync(alert.Id, cid, at, ct);
    }

    private async Task ResolveAsync(RuleRecord rule, string? hostId, string? sourceId, string fingerprint,
        DateTimeOffset at, CancellationToken ct)
    {
        var key = AlertKey(rule, hostId, sourceId, fingerprint);
        var live = await alerts.FindLiveAsync(key, ct);
        if (live is null) return;
        var resolved = live with { Status = AlertStatuses.Resolved, ResolvedAt = at, UpdatedAt = at };
        await alerts.UpdateAsync(resolved, ct);
        log.LogInformation("Alert {alertId} resolved: {title}", resolved.Id, resolved.Title);
    }

    /// <summary>Stable alert key: rule|host|-|source|-|fingerprint. Single
    /// construction site so heartbeat, threshold and reconcile paths cannot
    /// drift apart (DEFECTS.md D8).</summary>
    private static string AlertKey(RuleRecord rule, string? hostId, string? sourceId, string fingerprint)
        => $"{rule.Id}|{hostId ?? "-"}|{sourceId ?? "-"}|{fingerprint}";

    /// <summary>Internal Windows console/desktop-session accounts (DWM-1,
    /// UMFD-0, ...) that appear in 4624 LogonType-2 noise; never human logins.
    /// Ignored by any-user logon rules; explicitly listed users still match.</summary>
    private static readonly Regex NoiseAccount = new(@"^(?:dwm|umfd)(?:-\d+)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string EffectiveStatus(AlertRecord alert, DateTimeOffset at)
    {
        if (alert.Status == AlertStatuses.Resolved) return AlertStatuses.Resolved;
        if (alert.SilenceUntil is { } until && until > at) return AlertStatuses.Silenced;
        if (alert.AckAt is not null) return AlertStatuses.Acknowledged;
        return AlertStatuses.Active;
    }

    private static bool MatchSourceKind(RuleMatch match, string sourceKind)
        => match.SourceKinds.Count == 0 || match.SourceKinds.Contains(sourceKind);

    private static bool MatchEvent(RuleMatch match, ValidatedLogItem item)
    {
        if (match.Channel is not null && item.Channel != match.Channel) return false;
        if (match.EventIds.Count > 0 && (item.EventId is null || !match.EventIds.Contains(item.EventId.Value))) return false;
        // Native severity scale: 1 is most severe (Windows Level, RFC 5424).
        // severityMin matches events at least as severe as the threshold.
        if (match.SeverityMin is { } min && (item.Severity is null || item.Severity > min)) return false;
        if (match.MessagePattern is not null && (item.Message is null || !match.MessagePattern.IsMatch(item.Message))) return false;
        return true;
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return s.Length <= max ? s : s[..max] + "…";
    }

    internal static string RandomToken(int bytes)
        => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}

/// <summary>Typed match documents per rule type; parsed defensively from the
/// stored JSON (validated at rule CRUD time).</summary>
public sealed class RuleMatch
{
    public List<string> SourceKinds { get; } = [];
    public string? Channel { get; private set; }
    public List<long> EventIds { get; } = [];
    public int? SeverityMin { get; private set; }
    public Regex? MessagePattern { get; private set; }
    public List<string> ComponentTypes { get; } = [];
    public List<string> States { get; } = [];
    public bool IncludeRollup { get; private set; }
    public int SilenceAfterS { get; private set; } = 300;
    public string? Metric { get; private set; }
    public string Comparator { get; private set; } = "gt";
    public double Value { get; private set; }
    public List<string> Users { get; } = [];
    public string? Outcome { get; private set; }
    public List<int> LogonTypes { get; } = [];
    public List<string> ReplicationHealths { get; } = [];
    public List<string> ReplicationStates { get; } = [];

    public static RuleMatch? ParseEvent(string json) => Parse(json, m =>
    {
        m.SourceKinds.AddRange(ReadStrings(m, json, "sourceKinds"));
        m.Channel = ReadString(m, json, "channel");
        m.EventIds.AddRange(ReadLongs(m, json, "eventIds"));
        m.SeverityMin = ReadInt(m, json, "severityMin");
        var pat = ReadString(m, json, "messagePattern");
        if (pat is not null)
        {
            try { m.MessagePattern = new Regex(pat, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch (ArgumentException) { m.MessagePattern = null; }
        }
    });

    public static RuleMatch? ParseHealth(string json) => Parse(json, m =>
    {
        m.SourceKinds.AddRange(ReadStrings(m, json, "sourceKinds"));
        m.ComponentTypes.AddRange(ReadStrings(m, json, "componentTypes"));
        m.States.AddRange(ReadStrings(m, json, "states"));
        if (m.States.Count == 0) m.States.AddRange(["warning", "critical"]);
        m.IncludeRollup = ReadBool(m, json, "includeRollup") ?? true;
    });

    public static RuleMatch? ParseHeartbeat(string json) => Parse(json, m =>
    {
        m.SourceKinds.AddRange(ReadStrings(m, json, "sourceKinds"));
        m.SilenceAfterS = ReadInt(m, json, "silenceAfterS") ?? 300;
    });

    public static RuleMatch? ParseVmHeartbeat(string json) => Parse(json, m =>
    {
        m.SourceKinds.AddRange(ReadStrings(m, json, "sourceKinds"));
    });

    public static RuleMatch? ParseVmReplication(string json) => Parse(json, m =>
    {
        m.SourceKinds.AddRange(ReadStrings(m, json, "sourceKinds"));
        m.ReplicationHealths.AddRange(ReadStrings(m, json, "healths"));
        m.ReplicationStates.AddRange(ReadStrings(m, json, "states"));
        // Default matches the health-rule convention: empty selection = the
        // two alert-worthy health states. Fully qualified: the instance
        // property ReplicationHealths shadows the Domain static class.
        if (m.ReplicationHealths.Count == 0 && m.ReplicationStates.Count == 0)
            m.ReplicationHealths.AddRange([Hyveman.Domain.ReplicationHealths.Warning, Hyveman.Domain.ReplicationHealths.Critical]);
    });

    public static RuleMatch? ParseThreshold(string json) => Parse(json, m =>
    {
        m.SourceKinds.AddRange(ReadStrings(m, json, "sourceKinds"));
        m.Metric = ReadString(m, json, "metric");
        m.Comparator = ReadString(m, json, "comparator") ?? "gt";
        m.Value = ReadDouble(m, json, "value") ?? 0;
    });

    public static RuleMatch? ParseLogon(string json) => Parse(json, m =>
    {
        m.SourceKinds.AddRange(ReadStrings(m, json, "sourceKinds"));
        m.Outcome = ReadString(m, json, "outcome");
        m.Users.AddRange(ReadStrings(m, json, "users"));
        var types = Read(json, "logonTypes");
        if (types is JsonArray arr)
            m.LogonTypes.AddRange(arr.Select(x => x is JsonValue v && v.TryGetValue<int>(out var i) ? i : (int?)null)
                .Where(i => i is not null).Select(i => i!.Value));
    });

    /// <summary>vm_replication crossing test: health ∈ healths (when set) AND
    /// state ∈ states (when set). Null fields (VM not replicated) never
    /// match — a null health can't be warning/critical, and a null state
    /// can't be in any configured state list.</summary>
    public bool Matches(VmRecord vm)
    {
        if (ReplicationHealths.Count > 0
            && (vm.ReplicationHealth is null || !ReplicationHealths.Contains(vm.ReplicationHealth)))
            return false;
        if (ReplicationStates.Count > 0
            && (vm.ReplicationState is null || !ReplicationStates.Contains(vm.ReplicationState)))
            return false;
        return ReplicationHealths.Count > 0 || ReplicationStates.Count > 0;
    }

    private static RuleMatch? Parse(string json, Action<RuleMatch> apply)
    {
        try
        {
            var m = new RuleMatch();
            apply(m);
            return m;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadString(RuleMatch _, string json, string name)
        => Read(json, name)?.GetValue<string>();

    private static List<string> ReadStrings(RuleMatch _, string json, string name)
    {
        var n = Read(json, name);
        if (n is JsonArray arr)
            return arr.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return [s];
        return [];
    }

    private static List<long> ReadLongs(RuleMatch _, string json, string name)
    {
        var n = Read(json, name);
        if (n is not JsonArray arr) return [];
        return arr.Select(x => x is JsonValue v && v.TryGetValue<long>(out var l) ? l : (long?)null)
            .Where(l => l is not null).Select(l => l!.Value).ToList();
    }

    private static int? ReadInt(RuleMatch _, string json, string name) =>
        Read(json, name) is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static double? ReadDouble(RuleMatch _, string json, string name) =>
        Read(json, name) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static bool? ReadBool(RuleMatch _, string json, string name) =>
        Read(json, name) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static JsonNode? Read(string json, string name)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject o ? o[name] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
