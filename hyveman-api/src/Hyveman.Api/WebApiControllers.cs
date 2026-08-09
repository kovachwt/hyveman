using Hyveman.Application;
using Hyveman.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hyveman.Api;

/// <summary>Fleet overview (API.md §7.1).</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class OverviewController(OverviewService overview) : ControllerBase
{
    [HttpGet("overview")]
    public Task<OverviewResponse> Get(CancellationToken ct) => overview.BuildAsync(ct);
}

/// <summary>Hosts, health, history, VMs (API.md §7.1).</summary>
[ApiController]
[Route("api/v1/hosts")]
[Authorize]
public sealed class HostsController(
    HostsService hosts,
    IHealthStore health,
    IClock clock,
    ILogger<HostsController> log) : ControllerBase
{
    [HttpGet]
    public Task<List<HostDto>> List(CancellationToken ct) => hosts.ListAsync(ct);

    [HttpPost]
    public Task<HostDto> Create([FromBody] HostInput input, CancellationToken ct)
        => hosts.CreateAsync(input, Actor(), ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<HostDetailDto>> Get(string id, CancellationToken ct)
    {
        var host = await hosts.GetDetailAsync(id, ct);
        return host is null ? NotFound() : Ok(host);
    }

    [HttpPatch("{id}")]
    public Task<HostDto> Patch(string id, [FromBody] HostInput input, CancellationToken ct)
        => hosts.PatchAsync(id, input, Actor(), ct);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] bool confirm, CancellationToken ct)
    {
        if (!confirm)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["confirm"] = ["Host deletion requires confirm=true."],
            });
        await hosts.DeleteAsync(id, Actor(), ct);
        return NoContent();
    }

    /// <summary>Clears the accepted-on-first-use iDRAC certificate pin so the
    /// next poll can re-accept the certificate (API.md §9.1).</summary>
    [HttpDelete("{id}/idrac-cert")]
    public async Task<IActionResult> ClearIdracCert(string id, CancellationToken ct)
    {
        return await hosts.ClearIdracCertAsync(id, Actor(), ct) ? NoContent() : NotFound();
    }

    [HttpGet("{id}/vms")]
    public async Task<ActionResult<List<VmDto>>> Vms(string id, CancellationToken ct)
    {
        if (await hosts.GetAsync(id, ct) is null) return NotFound();
        var vms = await health.GetVmsAsync(id, ct);
        return vms.Select(v => new VmDto
        {
            Name = v.Name, State = v.State, HeartbeatOk = v.HeartbeatOk, CpuPct = v.CpuPct,
            MemMb = v.MemMb, LastSeen = v.LastSeen, Stale = v.Stale,
        }).ToList();
    }

    [HttpGet("{id}/health")]
    public Task<HostHealthResponse> Health(string id, CancellationToken ct)
        => hosts.GetHealthAsync(id, ct);

    [HttpGet("{id}/health-history")]
    public Task<HealthHistoryResponse> HealthHistory(string id, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, [FromQuery] string? resolution, CancellationToken ct)
        => hosts.GetHealthHistoryAsync(id, from, to, resolution, ct);

    private string Actor() => "admin";
}

/// <summary>Event search (API.md §7.2) + saved searches.</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class EventsController(EventsService events, SavedSearchesService saved) : ControllerBase
{
    [HttpGet("events")]
    public Task<EventSearchResponse> Search(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? hostId, [FromQuery] string? sourceId, [FromQuery] string? channel,
        [FromQuery] int? severityMin, [FromQuery] long? eventId, [FromQuery] string? q,
        [FromQuery] int? limit, [FromQuery] string? cursor, [FromQuery] string? sort,
        CancellationToken ct)
        => events.SearchAsync(new EventSearchParams(from, to, hostId, sourceId, channel,
            severityMin, eventId, q, limit, cursor, sort), ct);

    [HttpGet("events/{id:long}")]
    public async Task<ActionResult<EventDto>> Get(long id, CancellationToken ct)
    {
        var ev = await events.GetAsync(id, ct);
        return ev is null ? NotFound() : Ok(ev);
    }

    [HttpGet("saved-searches")]
    public async Task<ActionResult<List<SavedSearchDto>>> ListSearches(CancellationToken ct)
    {
        var list = await saved.ListAsync(ct);
        return list.Select(s => new SavedSearchDto
        {
            Id = s.Id, Name = s.Name, Filter = ParseFilter(s.FilterJson),
            CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt,
        }).ToList();
    }

    [HttpPost("saved-searches")]
    public async Task<ActionResult<SavedSearchDto>> CreateSearch([FromBody] SavedSearchInput input, CancellationToken ct)
    {
        var rec = await saved.CreateAsync(input.Name ?? "", input.Filter ?? [], Actor(), ct);
        return Created($"/api/v1/saved-searches/{rec.Id}", new SavedSearchDto
        {
            Id = rec.Id, Name = rec.Name, Filter = ParseFilter(rec.FilterJson),
            CreatedAt = rec.CreatedAt, UpdatedAt = rec.UpdatedAt,
        });
    }

    [HttpPatch("saved-searches/{id}")]
    public async Task<ActionResult<SavedSearchDto>> PatchSearch(string id, [FromBody] SavedSearchInput input, CancellationToken ct)
    {
        var rec = await saved.PatchAsync(id, input.Name, input.Filter, input.UpdatedAt, Actor(), ct);
        return new SavedSearchDto
        {
            Id = rec.Id, Name = rec.Name, Filter = ParseFilter(rec.FilterJson),
            CreatedAt = rec.CreatedAt, UpdatedAt = rec.UpdatedAt,
        };
    }

    [HttpDelete("saved-searches/{id}")]
    public async Task<IActionResult> DeleteSearch(string id, CancellationToken ct)
    {
        await saved.DeleteAsync(id, Actor(), ct);
        return NoContent();
    }

    private static Dictionary<string, object?> ParseFilter(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private string Actor() => "admin";
}

/// <summary>Sources, registration tokens, token revocation (API.md §7).</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class SourcesController(SourcesService sources) : ControllerBase
{
    [HttpGet("sources")]
    public Task<List<SourceDto>> List(CancellationToken ct) => sources.ListAsync(ct);

    [HttpPost("registration-tokens")]
    public Task<RegistrationTokenCreatedDto> CreateRegistrationToken(
        [FromBody] RegistrationTokenCreateRequest input, CancellationToken ct)
        => sources.CreateRegistrationTokenAsync(input.Kind, input.LifetimeMinutes, Actor(), ct);

    [HttpGet("registration-tokens")]
    public Task<List<RegistrationTokenDto>> ListRegistrationTokens(CancellationToken ct)
        => sources.ListRegistrationTokenDtosAsync(ct);

    [HttpPost("registration-tokens/{id}/revoke")]
    public async Task<IActionResult> RevokeRegistrationToken(string id, CancellationToken ct)
    {
        await sources.RevokeRegistrationTokenAsync(id, Actor(), ct);
        return NoContent();
    }

    [HttpPost("sources/{sourceId}/tokens/{tokenId}/revoke")]
    public async Task<IActionResult> RevokeToken(string sourceId, string tokenId, CancellationToken ct)
    {
        await sources.RevokeTokenAsync(sourceId, tokenId, Actor(), ct);
        return NoContent();
    }

    private string Actor() => "admin";
}
