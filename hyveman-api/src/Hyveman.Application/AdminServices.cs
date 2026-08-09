using System.Text.Json;
using Hyveman.Contracts;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>Maintenance windows CRUD (API.md §7.3): host-scoped or fleet-wide
/// suppression windows; overlapping windows are rejected.</summary>
public sealed class MaintenanceWindowsService(
    IMaintenanceWindowStore store,
    IHostStore hosts,
    IClock clock,
    IAuditStore audit)
{
    public Task<IReadOnlyList<MaintenanceWindowRecord>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public async Task<MaintenanceWindowRecord?> GetAsync(string id, CancellationToken ct) => await store.GetAsync(id, ct);

    public async Task<MaintenanceWindowRecord> CreateAsync(MaintenanceWindowInput input, string actor, CancellationToken ct)
    {
        var errors = Validate(input);
        if (errors.Count > 0) throw new ValidationProblemException(errors);
        if (input.HostId is { } hid && await hosts.GetAsync(hid, ct) is null)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["hostId"] = [$"host '{hid}' not found."],
            });
        var now = clock.UtcNow;
        var window = new MaintenanceWindowRecord("mw_" + HostsService.RandomId(18), input.HostId,
            input.Start!.Value, input.End!.Value, input.Reason, actor, now);
        await store.CreateAsync(window, ct);
        await audit.RecordAsync(actor, "maintenance_window.created", "maintenance_window", window.Id,
            JsonSerializer.Serialize(new { hostId = window.HostId, start = window.Start, end = window.End }), now, ct);
        return window;
    }

    public async Task<MaintenanceWindowRecord> PatchAsync(string id, MaintenanceWindowInput input, string actor, CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct) ?? throw new NotFoundException($"maintenance window '{id}' not found");
        if (input.UpdatedAt is { } expected && existing.CreatedAt != expected)
            throw new ConflictException($"maintenance window '{id}' was modified concurrently; reload and retry");
        var errors = Validate(input);
        if (errors.Count > 0) throw new ValidationProblemException(errors);
        var now = clock.UtcNow;
        var updated = existing with
        {
            HostId = input.HostId ?? existing.HostId,
            Start = input.Start ?? existing.Start,
            End = input.End ?? existing.End,
            Reason = input.Reason ?? existing.Reason,
        };
        var ok = await store.UpdateAsync(updated, existing.CreatedAt, ct);
        if (!ok) throw new ConflictException($"maintenance window '{id}' was modified concurrently; reload and retry");
        await audit.RecordAsync(actor, "maintenance_window.updated", "maintenance_window", id, null, now, ct);
        return updated;
    }

    public async Task DeleteAsync(string id, string actor, CancellationToken ct)
    {
        if (await store.GetAsync(id, ct) is null) throw new NotFoundException($"maintenance window '{id}' not found");
        await store.DeleteAsync(id, ct);
        await audit.RecordAsync(actor, "maintenance_window.deleted", "maintenance_window", id, null, clock.UtcNow, ct);
    }

    private Dictionary<string, List<string>> Validate(MaintenanceWindowInput input)
    {
        var errors = new Dictionary<string, List<string>>();
        if (input.Start is null) errors["start"] = ["Start is required."];
        if (input.End is null) errors["end"] = ["End is required."];
        if (input.Start is { } s && input.End is { } e && e <= s)
            errors["end"] = ["End must be after start."];
        if (input.Start is { } s2 && input.End is { } e2 && e2 - s2 > TimeSpan.FromDays(366))
            errors["end"] = ["Window is capped at 366 days."];
        return errors;
    }
}

/// <summary>Retention settings (API.md §7).</summary>
public sealed class SettingsService(ISettingsStore store, IClock clock, IAuditStore audit)
{
    public const string Key = "retention";

    public async Task<RetentionSettingsDto> GetRetentionAsync(CancellationToken ct)
    {
        var raw = await store.GetAsync(Key, ct);
        if (raw is null) return new RetentionSettingsDto();
        try
        {
            return JsonSerializer.Deserialize<RetentionSettingsDto>(raw) ?? new RetentionSettingsDto();
        }
        catch (JsonException)
        {
            return new RetentionSettingsDto();
        }
    }

    public async Task<RetentionSettingsDto> SetRetentionAsync(RetentionSettingsInput input, string actor, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        if (input.EventDays is { } ed && (ed < 1 || ed > 3650)) errors["eventDays"] = ["eventDays must be 1..3650."];
        if (input.MetricDays is { } md && (md < 1 || md > 3650)) errors["metricDays"] = ["metricDays must be 1..3650."];
        if (input.SnapshotDays is { } sd && (sd < 1 || sd > 3650)) errors["snapshotDays"] = ["snapshotDays must be 1..3650."];
        if (errors.Count > 0) throw new ValidationProblemException(errors);

        var current = await GetRetentionAsync(ct);
        var updated = new RetentionSettingsDto
        {
            EventDays = input.EventDays ?? current.EventDays,
            MetricDays = input.MetricDays ?? current.MetricDays,
            SnapshotDays = input.SnapshotDays ?? current.SnapshotDays,
        };
        var json = JsonSerializer.Serialize(updated);
        await store.SetAsync(Key, json, clock.UtcNow, ct);
        await audit.RecordAsync(actor, "settings.retention.updated", "settings", Key, json, clock.UtcNow, ct);
        return updated;
    }
}

/// <summary>Audit log queries (API.md §7).</summary>
public sealed class AuditService(IAuditStore store)
{
    public async Task<AuditListResponse> ListAsync(string? action, string? targetKind,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        var page = await store.ListAsync(new AuditQuery(action, targetKind, from, to,
            Math.Clamp(limit ?? 100, 1, 500), cursor), ct);
        return new AuditListResponse
        {
            Items = page.Select(a => new AuditEntryDto
            {
                Id = a.Id, Time = a.Time, Actor = a.Actor, Action = a.Action,
                TargetKind = a.TargetKind, TargetId = a.TargetId, DetailJson = a.DetailJson,
            }).ToList(),
            HasMore = page.Count >= Math.Clamp(limit ?? 100, 1, 500),
        };
    }
}
