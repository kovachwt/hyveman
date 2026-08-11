using Hyveman.Contracts;
using Hyveman.Domain;
using Microsoft.Extensions.Logging;

namespace Hyveman.Application;

/// <summary>GET /api/v1/overview (API.md §7.1): bounded aggregation for the
/// dashboard — never all events or full component history.</summary>
public sealed class OverviewService(
    IHostStore hosts,
    IHealthStore health,
    IAgentStatusStore agentStatus,
    IAlertStore alerts,
    ISourceStore sources,
    IPollStatusStore pollStatus,
    IClock clock,
    ILogger<OverviewService> log)
{
    public async Task<OverviewResponse> BuildAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var hostList = (await hosts.ListAsync(ct)).Where(h => h.Enabled).ToList();
        var componentsByHost = new Dictionary<string, IReadOnlyList<ComponentRecord>>();
        foreach (var h in hostList)
            componentsByHost[h.Id] = await health.GetComponentsAsync(h.Id, ct);

        var statusBySource = (await agentStatus.ListAllAsync(ct)).ToDictionary(s => s.SourceId);
        var sourceById = (await sources.ListAsync(ct)).ToDictionary(s => s.Id);
        var activeAlerts = await alerts.ListLiveAsync(ct);
        var alertsByHost = activeAlerts.GroupBy(a => a.HostId ?? "").ToDictionary(g => g.Key, g => g.ToList());

        var summary = new OverviewSummaryDto
        {
            ActiveAlerts = activeAlerts.Count,
            UnacknowledgedAlerts = (int)await alerts.CountUnacknowledgedAsync(ct),
        };

        var tiles = new List<HostTileDto>();
        foreach (var host in hostList)
        {
            var components = componentsByHost.GetValueOrDefault(host.Id) ?? [];
            var hardwareRollup = RollupOf(components); // iDRAC/Redfish components; Unknown if none.

            AgentStatusDto? agentDto = null;
            if (host.SourceId is { } sid && statusBySource.TryGetValue(sid, out var status))
            {
                agentDto = AgentStatusMapper.ToDto(status, now, hostSource: sourceById.GetValueOrDefault(sid));
                if (agentDto.Status == "silent") summary.SilentAgents++;
            }

            // OS reachability is an independent health signal: a silent agent
            // is critical, a reporting agent is ok, no agent at all is unknown.
            // (Hyper-V has no own state on the overview; VM health is served by
            // the host detail VMs tab, API.md §7.1.)
            var osState = agentDto is null
                ? HealthState.Unknown
                : agentDto.Status == "silent" ? HealthState.Critical
                : agentDto.Status == "online" ? HealthState.Ok
                : HealthState.Unknown;

            // The overview rollup combines every signal the dashboard can
            // actually observe. Hardware (iDRAC/Redfish) contributes only when
            // configured: a host with no iDRAC reads OK when its OS and agent
            // are healthy, not "unknown by omission." Worst signal wins.
            var idracConfigured = host.IdracUrl is not null;
            var rollup = idracConfigured
                ? HealthStates.Max(hardwareRollup, osState)
                : osState;
            var rollupWire = HealthStates.ToWire(rollup);

            switch (rollupWire)
            {
                case "critical": summary.Critical++; break;
                case "warning": summary.Warning++; break;
                case "ok": summary.Ok++; break;
                default: summary.Unknown++; break;
            }
            summary.Total++;

            // The rollup was evaluated at the latest contributing signal.
            var rollupTimes = new List<DateTimeOffset>();
            if (idracConfigured && components.Count > 0)
                rollupTimes.Add(components.Max(c => c.LastSeen));
            if (agentDto?.LastReceived is { } lastReceived)
                rollupTimes.Add(lastReceived);
            var rollupAt = rollupTimes.Count > 0 ? rollupTimes.Max() : (DateTimeOffset?)null;

            var poll = host.IdracUrl is null ? null : await pollStatus.GetAsync(host.Id, ct);
            tiles.Add(new HostTileDto
            {
                Id = host.Id,
                Name = host.Name,
                Kind = host.Kind,
                SourceId = host.SourceId,
                RollupState = rollupWire,
                RollupAt = rollupAt,
                // Hardware health only applies when iDRAC is configured; the
                // tile renders a "Not configured" affordance otherwise.
                HardwareState = idracConfigured ? HealthStates.ToWire(hardwareRollup) : null,
                OsState = HealthStates.ToWire(osState),
                HyperVState = null,
                Agent = agentDto,
                Idrac = host.IdracUrl is null ? null : new IdracStatusDto
                {
                    Configured = true,
                    // Real poll_status (API.md §9.1): the tile must show poll
                    // failures, not "never polled" forever while polls fail.
                    LastPoll = poll?.LastPoll,
                    LastPollOk = poll is { } ps && ps.Failures == 0,
                    LastError = poll?.LastError,
                },
                ActiveAlertCount = alertsByHost.GetValueOrDefault(host.Id)?.Count ?? 0,
            });
        }

        var recent = activeAlerts
            .OrderByDescending(a => a.LastSeen)
            .Take(10)
            .Select(a => AlertMapper.ToDto(a, hostList.FirstOrDefault(h => h.Id == a.HostId)?.Name))
            .ToList();

        return new OverviewResponse { GeneratedAt = now, Hosts = tiles, Summary = summary, RecentAlerts = recent };
    }

    /// <summary>Hardware (iDRAC/Redfish) component rollup for the overview
    /// tile's Hardware signal. Unknown when no components have been collected.
    /// </summary>
    internal static HealthState RollupOf(IReadOnlyList<ComponentRecord> components)
    {
        var state = HealthState.Unknown;
        foreach (var c in components)
            state = HealthStates.Max(state, c.State);
        return state;
    }
}

public static class AgentStatusMapper
{
    public static AgentStatusDto ToDto(AgentStatusRow s, DateTimeOffset now, Source? hostSource = null)
    {
        Dictionary<string, long>? counters = null;
        if (s.CountersJson is not null)
        {
            try
            {
                counters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(s.CountersJson);
            }
            catch (System.Text.Json.JsonException)
            {
                // counters are informational; never fail a dashboard on them
            }
        }
        var age = now - s.LastReceived;
        var status = age > TimeSpan.FromMinutes(5) ? "silent"
            : s.LastReceived > DateTimeOffset.MinValue ? "online" : "unknown";
        return new AgentStatusDto
        {
            SourceId = s.SourceId,
            Status = status,
            LastReceived = s.LastReceived,
            LastSentAt = s.LastSentAt,
            AgentVersion = s.AgentVersion,
            OsBuild = s.OsBuild,
            BootTime = s.BootTime,
            UptimeS = s.UptimeS,
            Degraded = s.Degraded,
            ConfigHash = s.ConfigHash,
            Counters = counters,
        };
    }
}

public static class AlertMapper
{
    public static AlertDto ToDto(AlertRecord a, string? hostName)
    {
        var status = a.SilenceUntil is { } until && until > DateTimeOffset.UtcNow ? AlertStatuses.Silenced : a.Status;
        return new AlertDto
        {
            Id = a.Id,
            RuleId = a.RuleId,
            HostId = a.HostId,
            HostName = hostName,
            SourceId = a.SourceId,
            Severity = a.Severity,
            Status = status,
            Title = a.Title,
            Detail = a.Detail,
            FirstSeen = a.FirstSeen,
            LastSeen = a.LastSeen,
            Count = a.Count,
            AckAt = a.AckAt,
            AckReason = a.AckReason,
            SilenceUntil = a.SilenceUntil,
            ResolvedAt = a.ResolvedAt,
        };
    }
}
