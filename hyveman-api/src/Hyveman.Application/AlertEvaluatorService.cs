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

    public static RuleMatch? ParseThreshold(string json) => Parse(json, m =>
    {
        m.SourceKinds.AddRange(ReadStrings(m, json, "sourceKinds"));
        m.Metric = ReadString(m, json, "metric");
        m.Comparator = ReadString(m, json, "comparator") ?? "gt";
        m.Value = ReadDouble(m, json, "value") ?? 0;
    });

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
