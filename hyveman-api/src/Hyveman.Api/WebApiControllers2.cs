using Hyveman.Application;
using Hyveman.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hyveman.Api;

/// <summary>Alerts + explicit actions (API.md §7.3).</summary>
[ApiController]
[Route("api/v1/alerts")]
[Authorize]
public sealed class AlertsController(AlertsService alerts) : ControllerBase
{
    [HttpGet]
    public Task<AlertListResponse> List([FromQuery] string? status, [FromQuery] string? hostId,
        [FromQuery] string? ruleId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
        => alerts.ListAsync(status, hostId, ruleId, from, to, limit, cursor, ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<AlertDto>> Get(string id, CancellationToken ct)
    {
        var alert = await alerts.GetAsync(id, ct);
        return alert is null ? NotFound() : Ok(alert);
    }

    [HttpPost("{id}/acknowledge")]
    public Task<AlertDto> Acknowledge(string id, [FromBody] AlertActionRequest? body, CancellationToken ct)
        => alerts.AcknowledgeAsync(id, body?.Reason, Actor(), ct);

    [HttpPost("{id}/unacknowledge")]
    public Task<AlertDto> Unacknowledge(string id, CancellationToken ct)
        => alerts.UnacknowledgeAsync(id, Actor(), ct);

    [HttpPost("{id}/silence")]
    public Task<AlertDto> Silence(string id, [FromBody] AlertActionRequest? body, CancellationToken ct)
    {
        if (body?.Until is not { } until)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["until"] = ["Silence requires an until timestamp."],
            });
        return alerts.SilenceAsync(id, until, body.Reason, Actor(), ct);
    }

    [HttpPost("{id}/unsilence")]
    public Task<AlertDto> Unsilence(string id, CancellationToken ct)
        => alerts.UnsilenceAsync(id, Actor(), ct);

    private string Actor() => User.Identity?.Name ?? "unknown";
}

/// <summary>Rules CRUD (API.md §7.3).</summary>
[ApiController]
[Route("api/v1/rules")]
[Authorize]
public sealed class RulesController(RulesService rules) : ControllerBase
{
    [HttpGet]
    public Task<List<RuleDto>> List(CancellationToken ct) => rules.ListAsync(ct);

    [HttpPost]
    public Task<RuleDto> Create([FromBody] RuleInput input, CancellationToken ct)
        => rules.CreateAsync(input, Actor(), ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<RuleDto>> Get(string id, CancellationToken ct)
    {
        var rule = await rules.GetAsync(id, ct);
        return rule is null ? NotFound() : Ok(rule);
    }

    [HttpPatch("{id}")]
    public Task<RuleDto> Patch(string id, [FromBody] RuleInput input, CancellationToken ct)
        => rules.PatchAsync(id, input, Actor(), ct);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] bool confirm, CancellationToken ct)
    {
        if (!confirm)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["confirm"] = ["Rule deletion requires confirm=true."],
            });
        await rules.DeleteAsync(id, Actor(), ct);
        return NoContent();
    }

    private string Actor() => User.Identity?.Name ?? "unknown";
}

/// <summary>Notification channels (API.md §7.4): secrets write-only.</summary>
[ApiController]
[Route("api/v1/notification-channels")]
[Authorize]
public sealed class NotificationChannelsController(ChannelsService channels) : ControllerBase
{
    [HttpGet]
    public Task<List<ChannelDto>> List(CancellationToken ct) => channels.ListAsync(ct);

    [HttpPost]
    public Task<ChannelDto> Create([FromBody] ChannelInput input, CancellationToken ct)
        => channels.CreateAsync(input, Actor(), ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<ChannelDto>> Get(string id, CancellationToken ct)
    {
        var channel = await channels.GetAsync(id, ct);
        return channel is null ? NotFound() : Ok(channel);
    }

    [HttpPatch("{id}")]
    public Task<ChannelDto> Patch(string id, [FromBody] ChannelInput input, CancellationToken ct)
        => channels.PatchAsync(id, input, Actor(), ct);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] bool confirm, CancellationToken ct)
    {
        if (!confirm)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["confirm"] = ["Channel deletion requires confirm=true."],
            });
        await channels.DeleteAsync(id, Actor(), ct);
        return NoContent();
    }

    [HttpPost("{id}/test")]
    public Task<ChannelTestResult> Test(string id, CancellationToken ct)
        => channels.TestAsync(id, ct);

    private string Actor() => User.Identity?.Name ?? "unknown";
}

/// <summary>Maintenance windows (API.md §7.3).</summary>
[ApiController]
[Route("api/v1/maintenance-windows")]
[Authorize]
public sealed class MaintenanceWindowsController(MaintenanceWindowsService windows) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<MaintenanceWindowDto>>> List(CancellationToken ct)
    {
        var list = await windows.ListAsync(ct);
        return list.Select(w => new MaintenanceWindowDto
        {
            Id = w.Id, HostId = w.HostId, Start = w.Start, End = w.End,
            Reason = w.Reason, CreatedBy = w.CreatedBy, CreatedAt = w.CreatedAt,
        }).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<MaintenanceWindowDto>> Create([FromBody] MaintenanceWindowInput input, CancellationToken ct)
    {
        var w = await windows.CreateAsync(input, Actor(), ct);
        return Created($"/api/v1/maintenance-windows/{w.Id}", new MaintenanceWindowDto
        {
            Id = w.Id, HostId = w.HostId, Start = w.Start, End = w.End,
            Reason = w.Reason, CreatedBy = w.CreatedBy, CreatedAt = w.CreatedAt,
        });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MaintenanceWindowDto>> Get(string id, CancellationToken ct)
    {
        var w = await windows.GetAsync(id, ct);
        if (w is null) return NotFound();
        return new MaintenanceWindowDto
        {
            Id = w.Id, HostId = w.HostId, Start = w.Start, End = w.End,
            Reason = w.Reason, CreatedBy = w.CreatedBy, CreatedAt = w.CreatedAt,
        };
    }

    [HttpPatch("{id}")]
    public async Task<ActionResult<MaintenanceWindowDto>> Patch(string id, [FromBody] MaintenanceWindowInput input, CancellationToken ct)
    {
        var w = await windows.PatchAsync(id, input, Actor(), ct);
        return new MaintenanceWindowDto
        {
            Id = w.Id, HostId = w.HostId, Start = w.Start, End = w.End,
            Reason = w.Reason, CreatedBy = w.CreatedBy, CreatedAt = w.CreatedAt,
        };
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await windows.DeleteAsync(id, Actor(), ct);
        return NoContent();
    }

    private string Actor() => User.Identity?.Name ?? "unknown";
}

/// <summary>Retention settings (API.md §7).</summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize]
public sealed class SettingsController(SettingsService settings) : ControllerBase
{
    [HttpGet("retention")]
    public Task<RetentionSettingsDto> GetRetention(CancellationToken ct) => settings.GetRetentionAsync(ct);

    [HttpPatch("retention")]
    public Task<RetentionSettingsDto> PatchRetention([FromBody] RetentionSettingsInput input, CancellationToken ct)
        => settings.SetRetentionAsync(input, Actor(), ct);

    private string Actor() => User.Identity?.Name ?? "unknown";
}

/// <summary>Audit log (API.md §7).</summary>
[ApiController]
[Route("api/v1/audit-log")]
[Authorize]
public sealed class AuditController(AuditService audit) : ControllerBase
{
    [HttpGet]
    public Task<AuditListResponse> List([FromQuery] string? action, [FromQuery] string? targetKind,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
        => audit.ListAsync(action, targetKind, from, to, limit, cursor, ct);
}

/// <summary>Per-user/per-day security-logon aggregates (DESIGN §4.1/§13 #5,
/// Phase 2): who logged on where and how often — 4624 successes (LogonType
/// 2/10), 4625 failures, 4740 lockouts.</summary>
[ApiController]
[Route("api/v1/logon-stats")]
[Authorize]
public sealed class LogonStatsController(LogonStatsService logonStats) : ControllerBase
{
    [HttpGet]
    public Task<LogonStatsResponse> List([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? sourceId, [FromQuery] string? user, [FromQuery] int? limit, CancellationToken ct)
        => logonStats.QueryAsync(from, to, sourceId, user, limit, ct);
}
