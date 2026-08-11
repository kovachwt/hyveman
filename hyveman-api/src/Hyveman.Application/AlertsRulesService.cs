using System.Text.Json;
using Hyveman.Contracts;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>Alert list + explicit actions (API.md §7.3): acknowledge, clear
/// acknowledgement, create/end silence. Every action is audited with actor,
/// target, previous/new state and reason.</summary>
public sealed class AlertsService(
    IAlertStore store,
    IHostStore hosts,
    IRuleStore rules,
    IClock clock,
    IAuditStore audit)
{
    public async Task<AlertListResponse> ListAsync(string? status, string? hostId, string? ruleId,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        var page = await store.ListAsync(new AlertQuery(status, hostId, ruleId, from, to,
            Math.Clamp(limit ?? 50, 1, 200), cursor), ct);
        var hostNames = (await hosts.ListAsync(ct)).ToDictionary(h => h.Id, h => h.Name);
        var ruleNames = (await rules.ListAsync(ct)).ToDictionary(r => r.Id, r => r.Name);
        var now = clock.UtcNow;
        var items = new List<AlertDto>();
        foreach (var a in page)
        {
            var dto = AlertMapper.ToDto(a with { Status = Effective(a, now) },
                a.HostId is not null ? hostNames.GetValueOrDefault(a.HostId) : null);
            dto.RuleName = a.RuleId is not null ? ruleNames.GetValueOrDefault(a.RuleId) : null;
            items.Add(dto);
        }
        return new AlertListResponse
        {
            Items = items,
            HasMore = page.Count >= Math.Clamp(limit ?? 50, 1, 200),
        };
    }

    public async Task<AlertDto?> GetAsync(string id, CancellationToken ct)
    {
        var a = await store.GetAsync(id, ct);
        if (a is null) return null;
        var hostName = a.HostId is null ? null : (await hosts.GetAsync(a.HostId, ct))?.Name;
        var ruleName = a.RuleId is null ? null : (await rules.GetAsync(a.RuleId, ct))?.Name;
        var dto = AlertMapper.ToDto(a with { Status = Effective(a, clock.UtcNow) }, hostName);
        dto.RuleName = ruleName;
        return dto;
    }

    public async Task<AlertDto> AcknowledgeAsync(string id, string? reason, string actor, CancellationToken ct)
    {
        var a = await store.GetAsync(id, ct) ?? throw new NotFoundException($"alert '{id}' not found");
        if (a.Status == AlertStatuses.Resolved)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["status"] = ["Cannot acknowledge a resolved alert."],
            });
        var now = clock.UtcNow;
        var updated = a with { AckAt = now, AckReason = reason, Status = Effective(a with { AckAt = now, AckReason = reason }, now), UpdatedAt = now };
        await store.UpdateAsync(updated, ct);
        await audit.RecordAsync(actor, "alert.acknowledged", "alert", id,
            JsonSerializer.Serialize(new { reason }), now, ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<AlertDto> UnacknowledgeAsync(string id, string actor, CancellationToken ct)
    {
        var a = await store.GetAsync(id, ct) ?? throw new NotFoundException($"alert '{id}' not found");
        if (a.Status == AlertStatuses.Resolved)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["status"] = ["Cannot unacknowledge a resolved alert."],
            });
        var now = clock.UtcNow;
        var updated = a with { AckAt = null, AckReason = null, Status = Effective(a with { AckAt = null, AckReason = null }, now), UpdatedAt = now };
        await store.UpdateAsync(updated, ct);
        await audit.RecordAsync(actor, "alert.unacknowledged", "alert", id, null, now, ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<AlertDto> SilenceAsync(string id, DateTimeOffset until, string? reason, string actor, CancellationToken ct)
    {
        var a = await store.GetAsync(id, ct) ?? throw new NotFoundException($"alert '{id}' not found");
        if (a.Status == AlertStatuses.Resolved)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["status"] = ["Cannot silence a resolved alert."],
            });
        var now = clock.UtcNow;
        if (until <= now)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["until"] = ["Silence end must be in the future."],
            });
        var updated = a with { SilenceUntil = until, Status = AlertStatuses.Silenced, UpdatedAt = now };
        await store.UpdateAsync(updated, ct);
        await audit.RecordAsync(actor, "alert.silenced", "alert", id,
            JsonSerializer.Serialize(new { until, reason }), now, ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<AlertDto> UnsilenceAsync(string id, string actor, CancellationToken ct)
    {
        var a = await store.GetAsync(id, ct) ?? throw new NotFoundException($"alert '{id}' not found");
        var now = clock.UtcNow;
        var updated = a with { SilenceUntil = null, Status = Effective(a with { SilenceUntil = null }, now), UpdatedAt = now };
        await store.UpdateAsync(updated, ct);
        await audit.RecordAsync(actor, "alert.unsilenced", "alert", id, null, now, ct);
        return (await GetAsync(id, ct))!;
    }

    internal static string Effective(AlertRecord a, DateTimeOffset now)
    {
        if (a.Status == AlertStatuses.Resolved) return AlertStatuses.Resolved;
        if (a.SilenceUntil is { } until && until > now) return AlertStatuses.Silenced;
        if (a.AckAt is not null) return AlertStatuses.Acknowledged;
        return AlertStatuses.Active;
    }
}

/// <summary>Rule CRUD with type-specific match-document validation (API.md §7.3).</summary>
public sealed class RulesService(IRuleStore store, INotificationChannelStore channels, IClock clock, IAuditStore audit)
{
    public async Task<List<RuleDto>> ListAsync(CancellationToken ct)
    {
        var rules = await store.ListAsync(ct);
        var result = new List<RuleDto>();
        foreach (var r in rules)
            result.Add(await ToDtoAsync(r, ct));
        return result;
    }

    public async Task<RuleDto?> GetAsync(string id, CancellationToken ct)
    {
        var r = await store.GetAsync(id, ct);
        return r is null ? null : await ToDtoAsync(r, ct);
    }

    public async Task<RuleDto> CreateAsync(RuleInput input, string actor, CancellationToken ct)
    {
        var errors = Validate(input);
        if (errors.Count > 0) throw new ValidationProblemException(errors);
        var now = clock.UtcNow;
        var match = JsonSerializer.Serialize(input.Match ?? new Dictionary<string, object?>());
        var rule = new RuleRecord("rul_" + HostsService.RandomId(18), input.Name!.Trim(), input.Type!,
            match, input.Severity!, input.CooldownS ?? 0, input.AutoResolveAfterS, input.Enabled ?? true, now, now);
        await store.CreateAsync(rule, ct);
        if (input.ChannelIds is not null)
            await store.SetChannelsAsync(rule.Id, input.ChannelIds, ct);
        await audit.RecordAsync(actor, "rule.created", "rule", rule.Id, match, now, ct);
        return (await GetAsync(rule.Id, ct))!;
    }

    public async Task<RuleDto> PatchAsync(string id, RuleInput input, string actor, CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct) ?? throw new NotFoundException($"rule '{id}' not found");
        if (input.UpdatedAt is { } expected && existing.UpdatedAt != expected)
            throw new ConflictException($"rule '{id}' was modified concurrently; reload and retry");
        var errors = Validate(input);
        if (errors.Count > 0) throw new ValidationProblemException(errors);
        var now = clock.UtcNow;
        var updated = existing with
        {
            Name = input.Name?.Trim() ?? existing.Name,
            Type = input.Type ?? existing.Type,
            MatchJson = input.Match is null ? existing.MatchJson : JsonSerializer.Serialize(input.Match),
            Severity = input.Severity ?? existing.Severity,
            CooldownS = input.CooldownS ?? existing.CooldownS,
            AutoResolveAfterS = input.AutoResolveAfterS ?? existing.AutoResolveAfterS,
            Enabled = input.Enabled ?? existing.Enabled,
            UpdatedAt = now,
        };
        var ok = await store.UpdateAsync(updated, existing.UpdatedAt, ct);
        if (!ok) throw new ConflictException($"rule '{id}' was modified concurrently; reload and retry");
        if (input.ChannelIds is not null)
            await store.SetChannelsAsync(id, input.ChannelIds, ct);
        await audit.RecordAsync(actor, "rule.updated", "rule", id, updated.MatchJson, now, ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task DeleteAsync(string id, string actor, CancellationToken ct)
    {
        if (await store.GetAsync(id, ct) is null) throw new NotFoundException($"rule '{id}' not found");
        await store.DeleteAsync(id, ct);
        await audit.RecordAsync(actor, "rule.deleted", "rule", id, null, clock.UtcNow, ct);
    }

    private Dictionary<string, List<string>> Validate(RuleInput input)
    {
        var errors = new Dictionary<string, List<string>>();
        if (input.Name is not null && string.IsNullOrWhiteSpace(input.Name)) errors["name"] = ["Name is required."];
        if (input.Type is not null && !RuleTypes.Known.Contains(input.Type))
            errors["type"] = [$"type must be one of: {string.Join(", ", RuleTypes.Known)}."];
        if (input.Severity is not null && !new[] { "info", "warning", "critical" }.Contains(input.Severity))
            errors["severity"] = ["severity must be info, warning or critical."];
        if (input.CooldownS is { } cd && cd < 0) errors["cooldownS"] = ["cooldownS must be >= 0."];
        if (input.AutoResolveAfterS is { } ar && ar < 0) errors["autoResolveAfterS"] = ["autoResolveAfterS must be >= 0."];
        var type = input.Type;
        if (input.Match is not null && type is not null)
        {
            var m = input.Match;
            switch (type)
            {
                case RuleTypes.Event:
                    var hasCriterion = m.ContainsKey("channel") || m.ContainsKey("eventIds") || m.ContainsKey("severityMin") || m.ContainsKey("messagePattern");
                    if (!hasCriterion) errors["match"] = ["event rules need at least one of channel, eventIds, severityMin or messagePattern."];
                    break;
                case RuleTypes.Heartbeat:
                    if (m.GetValueOrDefault("silenceAfterS") is not JsonElement { ValueKind: JsonValueKind.Number })
                        errors["match.silenceAfterS"] = ["silenceAfterS (seconds) is required for heartbeat rules."];
                    break;
                case RuleTypes.VmHeartbeat:
                    // No required match fields: the rule fires on any OK→lost
                    // transition for running VMs (optional sourceKinds scope).
                    break;
                case RuleTypes.VmReplication:
                    // Optional healths[]/states[]; both empty defaults to
                    // healths=["warning","critical"] server-side. Validate
                    // enum membership so a typo is rejected at CRUD time.
                    if (m.GetValueOrDefault("healths") is JsonElement { ValueKind: JsonValueKind.Array } healths
                        && healths.EnumerateArray().Any(h => h.ValueKind != JsonValueKind.String || !ReplicationHealths.Known.Contains(h.GetString()!)))
                        errors["match.healths"] = [$"healths must be strings from: {string.Join(", ", ReplicationHealths.Known)}."];
                    if (m.GetValueOrDefault("states") is JsonElement { ValueKind: JsonValueKind.Array } states
                        && states.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.String || !ReplicationStates.Known.Contains(s.GetString()!)))
                        errors["match.states"] = [$"states must be strings from: {string.Join(", ", ReplicationStates.Known)}."];
                    break;
                case RuleTypes.Threshold:
                    // NOTE: values in a JSON-deserialized Dictionary<string, object?>
                    // are JsonElement, not CLR strings (the old `is not string` check
                    // always failed, so every threshold rule was rejected).
                    var metric = m.GetValueOrDefault("metric") switch
                    {
                        string s => s,
                        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                        _ => null,
                    };
                    if (string.IsNullOrEmpty(metric))
                        errors["match.metric"] = ["metric is required for threshold rules."];
                    if (m.GetValueOrDefault("value") is not JsonElement { ValueKind: JsonValueKind.Number })
                        errors["match.value"] = ["value is required for threshold rules."];
                    if (m.GetValueOrDefault("comparator") is JsonElement { ValueKind: JsonValueKind.String } cmp
                        && !new[] { "gt", "gte", "lt", "lte", "eq" }.Contains(cmp.GetString()))
                        errors["match.comparator"] = ["comparator must be gt, gte, lt, lte or eq."];
                    break;
                case RuleTypes.Logon:
                    var outcome = m.GetValueOrDefault("outcome") switch
                    {
                        string s2 => s2,
                        JsonElement { ValueKind: JsonValueKind.String } je2 => je2.GetString(),
                        _ => null,
                    };
                    if (outcome is null || !LogonOutcomes.Known.Contains(outcome))
                        errors["match.outcome"] = ["outcome (success, failure or lockout) is required for logon rules."];
                    if (m.GetValueOrDefault("users") is JsonElement { ValueKind: JsonValueKind.Array } users
                        && users.EnumerateArray().Any(u => u.ValueKind != JsonValueKind.String))
                        errors["match.users"] = ["users must be an array of account names."];
                    if (m.GetValueOrDefault("logonTypes") is JsonElement { ValueKind: JsonValueKind.Array } types
                        && types.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.Number))
                        errors["match.logonTypes"] = ["logonTypes must be an array of numbers."];
                    break;
            }
        }
        return errors;
    }

    private async Task<RuleDto> ToDtoAsync(RuleRecord r, CancellationToken ct)
    {
        Dictionary<string, object?>? match = null;
        try { match = JsonSerializer.Deserialize<Dictionary<string, object?>>(r.MatchJson); }
        catch (JsonException) { match = []; }
        var channelIds = await store.GetChannelIdsAsync(r.Id, ct);
        return new RuleDto
        {
            Id = r.Id, Name = r.Name, Type = r.Type, Match = match ?? [], Severity = r.Severity,
            CooldownS = r.CooldownS, AutoResolveAfterS = r.AutoResolveAfterS, Enabled = r.Enabled, ChannelIds = channelIds.ToList(),
            CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
        };
    }
}
