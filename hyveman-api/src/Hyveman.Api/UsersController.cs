using System.Security.Claims;
using Hyveman.Application;
using Hyveman.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hyveman.Api;

/// <summary>User administration + invite links (docs/MULTI-USER.md §8/§9).
/// All users have equal permissions; every mutation is audited with the
/// acting user's real name.</summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
public sealed class UsersController(
    UsersService users,
    IOptionsMonitor<HyvemanOptions> opts) : ControllerBase
{
    [HttpGet]
    public Task<List<UserDto>> List(CancellationToken ct) => users.ListAsync(ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<UserDetailDto>> Get(string id, CancellationToken ct)
    {
        var user = await users.GetAsync(id, ct);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost("{id}/disable")]
    public async Task<IActionResult> Disable(string id, CancellationToken ct)
    {
        await users.DisableAsync(id, UserId(), ct);
        return NoContent();
    }

    [HttpPost("{id}/enable")]
    public async Task<IActionResult> Enable(string id, CancellationToken ct)
    {
        await users.EnableAsync(id, UserId(), ct);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] bool confirm, CancellationToken ct)
    {
        await users.DeleteAsync(id, confirm, UserId(), ct);
        return NoContent();
    }

    [HttpDelete("{id}/passkeys/{passkeyId}")]
    public async Task<IActionResult> RemovePasskey(string id, string passkeyId,
        [FromQuery] bool confirm, CancellationToken ct)
    {
        if (!confirm)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["confirm"] = ["Passkey removal requires confirm=true."],
            });
        var detail = await users.GetAsync(id, ct);
        if (detail is null) return NotFound();
        await users.RemovePasskeyAsync(passkeyId, UserId(), ct);
        return NoContent();
    }

    [HttpGet("invitations")]
    public Task<List<InvitationDto>> ListInvitations(CancellationToken ct)
        => users.ListInvitationsAsync(ct);

    [HttpPost("invitations")]
    public async Task<ActionResult<InvitationCreatedDto>> CreateInvitation(
        [FromBody] InvitationCreateRequest? input, CancellationToken ct)
    {
        var created = await users.CreateInvitationAsync(input?.ExpiresInMinutes, UserId(), ct);
        created.Url = InviteUrl(created.Token);
        return Created($"/api/v1/users/invitations/{created.Id}", created);
    }

    [HttpPost("invitations/{id}/revoke")]
    public async Task<IActionResult> RevokeInvitation(string id, CancellationToken ct)
    {
        await users.RevokeInvitationAsync(id, UserId(), ct);
        return NoContent();
    }

    private string UserId() =>
        HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    /// <summary>The shareable accept-invite link. The raw token rides in the
    /// URL fragment (#token=...) so it never reaches server logs or Referer
    /// headers; it is sent to the API only in request bodies.</summary>
    private string InviteUrl(string rawToken)
    {
        var current = opts.CurrentValue;
        var origin = !string.IsNullOrEmpty(current.PublicOrigin)
            ? current.PublicOrigin
            : current.WebAuthnExpectedOrigin;
        var baseUrl = string.IsNullOrEmpty(origin) ? "" : origin.TrimEnd('/');
        return $"{baseUrl}/accept-invite#token={rawToken}";
    }
}
