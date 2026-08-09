using System.Text.Json;
using Hyveman.Contracts;
using Hyveman.Domain;

namespace Hyveman.Application;

public sealed class ConflictException(string message) : Exception(message);
public sealed class NotFoundException(string message) : Exception(message);
public sealed class ValidationProblemException(Dictionary<string, List<string>> errors) : Exception("Validation failed")
{
    public Dictionary<string, List<string>> Errors { get; } = errors;
}

/// <summary>Hosts CRUD + detail + health history (API.md §7.1). Hosts without
/// an agent are valid hardware records; sources and hosts are not implicitly
/// interchangeable.</summary>
public sealed class HostsService(
    IHostStore store,
    IHealthStore health,
    IAgentStatusStore agentStatus,
    IAlertStore alerts,
    IEventStore events,
    IClock clock,
    IAuditStore audit,
    ICredentialVault vault)
{
    public async Task<List<HostDto>> ListAsync(CancellationToken ct)
    {
        var hosts = await store.ListAsync(ct);
        var creds = await vault.ListAsync(ct);
        return hosts.Select(h => Map(h, creds.Any(c => c.Id == h.IdracCredRef))).ToList();
    }

    public async Task<HostDto?> GetAsync(string id, CancellationToken ct)
    {
        var host = await store.GetAsync(id, ct);
        if (host is null) return null;
        var creds = await vault.ListAsync(ct);
        return Map(host, creds.Any(c => c.Id == host.IdracCredRef));
    }

    public async Task<HostDetailDto?> GetDetailAsync(string id, CancellationToken ct)
    {
        var host = await store.GetAsync(id, ct) ?? throw new NotFoundException($"host '{id}' not found");
        var creds = await vault.ListAsync(ct);
        var components = await health.GetComponentsAsync(id, ct);
        var metrics = await health.GetLatestMetricsAsync(id, maxPerName: 1, ct);
        var liveAlerts = await alerts.ListAsync(new AlertQuery(null, id, null, null, null, 20, null), ct);
        var recentEvents = await events.SearchAsync(new EventQuery(
            From: null, To: null, HostId: id, SourceId: null, Channel: null,
            SeverityMin: null, EventId: null, Q: null, Limit: 10, Cursor: null, Sort: "desc"), ct);
        AgentStatusDto? agent = null;
        if (host.SourceId is { } sid && await agentStatus.GetAsync(sid, ct) is { } status)
            agent = AgentStatusMapper.ToDto(status, clock.UtcNow);

        return new HostDetailDto
        {
            Id = host.Id,
            Name = host.Name,
            Kind = host.Kind,
            SourceId = host.SourceId,
            IdracUrl = host.IdracUrl,
            IdracCredentialSet = host.IdracCredRef is not null,
            Enabled = host.Enabled,
            Notes = host.Notes,
            UpdatedAt = host.UpdatedAt,
            CreatedAt = host.CreatedAt,
            RollupState = OverviewService.RollupOf(components),
            RollupAt = components.Count > 0 ? components.Max(c => c.LastSeen) : null,
            Components = components.Select(c => new ComponentDto
            {
                Type = c.Type, Name = c.Name, State = HealthStates.ToWire(c.State),
                Detail = c.Detail, LastSeen = c.LastSeen,
            }).OrderBy(c => c.Type).ToList(),
            LatestMetrics = metrics.Select(m => new MetricDto { Name = m.Name, Value = m.Value, Unit = m.Unit, Time = m.Time }).ToList(),
            RecentAlerts = liveAlerts.Select(a => AlertMapper.ToDto(a, host.Name)).ToList(),
            RecentEvents = recentEvents.Items.Select(EventMapper.ToDto).ToList(),
            Agent = agent,
        };
    }

    public async Task<HostDto> CreateAsync(HostInput input, string actor, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        if (string.IsNullOrWhiteSpace(input.Name)) errors["name"] = ["Name is required."];
        if (input.SourceId is { } sid && await store.GetBySourceAsync(sid, ct) is { } existing)
            errors["sourceId"] = [$"source '{sid}' is already associated with host '{existing.Name}'"];
        if (input.IdracUrl is { Length: > 0 } url && !IsAllowedIdracUrl(url))
            errors["idracUrl"] = ["iDRAC URL must be https:// and contain no userinfo or fragment."];
        if (errors.Count > 0) throw new ValidationProblemException(errors);

        var now = clock.UtcNow;
        string? credRef = null;
        if (input.IdracUsername is { Length: > 0 } || input.IdracPassword is { Length: > 0 })
        {
            if (input.IdracUsername is not { Length: > 0 } || input.IdracPassword is not { Length: > 0 })
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["idracCredentials"] = ["Both username and password are required when setting iDRAC credentials."],
                });
            credRef = await vault.StoreAsync(CredentialKinds.Idrac, $"{input.Name} iDRAC",
                JsonSerializer.Serialize(new { username = input.IdracUsername, password = input.IdracPassword }), ct);
        }

        var host = new HostRecord(
            Id: "hst_" + RandomId(18), Name: input.Name.Trim(), Kind: input.Kind ?? "windows-server",
            SourceId: input.SourceId, IdracUrl: NormalizeUrl(input.IdracUrl), IdracCredRef: credRef,
            Enabled: input.Enabled ?? true, Notes: input.Notes, UpdatedAt: now, CreatedAt: now);
        await store.CreateAsync(host, ct);
        await audit.RecordAsync(actor, "host.created", "host", host.Id, JsonSerializer.Serialize(new { host.Name }), now, ct);
        return Map(host, credRef is not null);
    }

    public async Task<HostDto> PatchAsync(string id, HostInput input, string actor, CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct) ?? throw new NotFoundException($"host '{id}' not found");
        var errors = new Dictionary<string, List<string>>();
        if (input.UpdatedAt is { } expected && existing.UpdatedAt != expected)
            throw new ConflictException($"host '{id}' was modified concurrently; reload and retry");
        if (input.SourceId is { } sid && sid != existing.SourceId && await store.GetBySourceAsync(sid, ct) is { } other)
            errors["sourceId"] = [$"source '{sid}' is already associated with host '{other.Name}'"];
        if (input.IdracUrl is { Length: > 0 } url && !IsAllowedIdracUrl(url))
            errors["idracUrl"] = ["iDRAC URL must be https:// and contain no userinfo or fragment."];
        if (errors.Count > 0) throw new ValidationProblemException(errors);

        var now = clock.UtcNow;
        var name = input.Name?.Trim() ?? existing.Name;
        var credRef = existing.IdracCredRef;
        if (input.IdracUsername is { Length: > 0 } || input.IdracPassword is { Length: > 0 })
        {
            if (input.IdracUsername is not { Length: > 0 } || input.IdracPassword is not { Length: > 0 })
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["idracCredentials"] = ["Both username and password are required when setting iDRAC credentials."],
                });
            var plaintext = JsonSerializer.Serialize(new { username = input.IdracUsername, password = input.IdracPassword });
            if (credRef is null)
                credRef = await vault.StoreAsync(CredentialKinds.Idrac, $"{name} iDRAC", plaintext, ct);
            else
                await vault.UpdateAsync(credRef, plaintext, ct);
        }

        var updated = existing with
        {
            Name = name,
            Kind = input.Kind ?? existing.Kind,
            SourceId = input.SourceId ?? existing.SourceId,
            IdracUrl = input.IdracUrl is null ? existing.IdracUrl : NormalizeUrl(input.IdracUrl),
            IdracCredRef = credRef,
            Enabled = input.Enabled ?? existing.Enabled,
            Notes = input.Notes ?? existing.Notes,
            UpdatedAt = now,
        };
        var ok = await store.UpdateAsync(updated, existing.UpdatedAt, ct);
        if (!ok) throw new ConflictException($"host '{id}' was modified concurrently; reload and retry");
        await audit.RecordAsync(actor, "host.updated", "host", id,
            JsonSerializer.Serialize(new { name, sourceId = updated.SourceId }), now, ct);
        return Map(updated, credRef is not null);
    }

    public async Task DeleteAsync(string id, string actor, CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct) ?? throw new NotFoundException($"host '{id}' not found");
        await store.DeleteAsync(id, ct);
        if (existing.IdracCredRef is not null)
            await vault.DeleteAsync(existing.IdracCredRef, ct);
        await alerts.ResolveForHostAsync(id, clock.UtcNow, ct);
        await audit.RecordAsync(actor, "host.deleted", "host", id, null, clock.UtcNow, ct);
    }

    public async Task<HostHealthResponse> GetHealthAsync(string id, CancellationToken ct)
    {
        var host = await store.GetAsync(id, ct) ?? throw new NotFoundException($"host '{id}' not found");
        var components = await health.GetComponentsAsync(id, ct);
        var metrics = await health.GetLatestMetricsAsync(id, maxPerName: 1, ct);
        var now = clock.UtcNow;
        var snapshots = await health.GetSnapshotsAsync(id, now.AddDays(-7), now, limit: 200, ct);
        return new HostHealthResponse
        {
            HostId = id,
            RollupState = OverviewService.RollupOf(components),
            RollupAt = components.Count > 0 ? components.Max(c => c.LastSeen) : null,
            Components = components.Select(c => new ComponentDto
            {
                Type = c.Type, Name = c.Name, State = HealthStates.ToWire(c.State),
                Detail = c.Detail, LastSeen = c.LastSeen,
            }).OrderBy(c => c.Type).ToList(),
            LatestMetrics = metrics.Select(m => new MetricDto { Name = m.Name, Value = m.Value, Unit = m.Unit, Time = m.Time }).ToList(),
            RecentSnapshots = snapshots.Select(s => new HealthSnapshotDto { Time = s.Time, RollupState = s.RollupState }).ToList(),
        };
    }

    public async Task<HealthHistoryResponse> GetHealthHistoryAsync(string id, DateTimeOffset? from, DateTimeOffset? to, string? resolution, CancellationToken ct)
    {
        if (await store.GetAsync(id, ct) is null) throw new NotFoundException($"host '{id}' not found");
        var to2 = to ?? clock.UtcNow;
        var from2 = from ?? to2.AddDays(-7);
        if (to2 - from2 > TimeSpan.FromDays(366))
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["from"] = ["Time range is capped at 366 days."],
            });
        var snapshots = await health.GetSnapshotsAsync(id, from2, to2, limit: 5000, ct);
        var metrics = await GetMetricsInRangeAsync(id, from2, to2, ct);
        var points = new List<HealthHistoryPoint>();
        var seen = new HashSet<long>();
        foreach (var s in snapshots)
        {
            var key = s.Time.ToUnixTimeSeconds();
            if (!seen.Add(key)) continue;
            points.Add(new HealthHistoryPoint { Time = s.Time, RollupState = s.RollupState });
        }
        foreach (var m in metrics)
        {
            var key = m.Time.ToUnixTimeSeconds();
            var point = points.FirstOrDefault(p => p.Time.ToUnixTimeSeconds() == key);
            if (point is null)
            {
                point = new HealthHistoryPoint { Time = m.Time, RollupState = "unknown" };
                points.Add(point);
            }
            if (m.Name.StartsWith("temperature") && (point.TemperatureMaxC is null || m.Value > point.TemperatureMaxC))
                point.TemperatureMaxC = m.Value;
            if (m.Name.StartsWith("power") && (point.PowerWatts is null || m.Value > point.PowerWatts))
                point.PowerWatts = m.Value;
        }
        return new HealthHistoryResponse
        {
            HostId = id, From = from2, To = to2,
            Resolution = resolution ?? "auto",
            Points = points.OrderBy(p => p.Time).ToList(),
        };
    }

    private Task<IReadOnlyList<MetricRecord>> GetMetricsInRangeAsync(string hostId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => health.GetMetricsInRangeAsync(hostId, from, to, ct);

    internal static bool IsAllowedIdracUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    }

    internal static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = new Uri(url, UriKind.Absolute);
        return u.AbsoluteUri.TrimEnd('/');
    }

    internal static string RandomId(int bytes) =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    internal static HostDto Map(HostRecord h, bool credSet) => new()
    {
        Id = h.Id, Name = h.Name, Kind = h.Kind, SourceId = h.SourceId, IdracUrl = h.IdracUrl,
        IdracCredentialSet = credSet, Enabled = h.Enabled, Notes = h.Notes,
        UpdatedAt = h.UpdatedAt, CreatedAt = h.CreatedAt,
    };
}
