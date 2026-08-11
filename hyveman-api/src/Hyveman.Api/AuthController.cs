using System.Security.Claims;
using System.Text.Json;
using Hyveman.Application;
using Hyveman.Contracts;
using Hyveman.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hyveman.Api;

/// <summary>Passkey-only web authentication (API.md §8.1, docs/MULTI-USER.md):
/// the API owns all ceremony state; the browser receives only the options and
/// posts the credential response back. Session cookie issued on successful
/// verify. Registration has three modes — first-run setup (trusted network,
/// no users), invite acceptance (single-use invite token), and authenticated
/// add (session user's own new key).</summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    IWebAuthnService webauthn,
    UsersService users,
    ISessionStore sessions,
    IUserStore userStore,
    IClock clock,
    RateLimiterRegistry rateLimiter,
    IOptionsMonitor<SessionAuthOptions> sessionOptions,
    ILogger<AuthController> log) : ControllerBase
{
    private const string SessionCookie = SessionAuthOptions.CookieName;

    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<ActionResult<SessionResponse>> Session(CancellationToken ct)
    {
        var authenticated = HttpContext.User.Identity?.IsAuthenticated == true;
        var setupRequired = await webauthn.IsSetupRequiredAsync(ct);
        SessionUserDto? user = null;
        if (authenticated)
        {
            var userId = HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var record = userId is null ? null : await userStore.GetAsync(userId, ct);
            if (record is null)
            {
                // The session was stamped from a user that no longer exists;
                // treat as unauthenticated rather than a half-authenticated state.
                authenticated = false;
            }
            else
            {
                user = new SessionUserDto
                {
                    Id = record.Id,
                    Name = record.Name,
                    DisplayName = record.DisplayName,
                };
            }
        }
        return new SessionResponse
        {
            Authenticated = authenticated,
            SetupRequired = setupRequired && !authenticated,
            User = user,
        };
    }

    [HttpPost("passkeys/login/options")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginOptions(CancellationToken ct)
    {
        if (!AcquireAuthBudget(out var retryAfter))
            return TooManyRequests(retryAfter);
        // Serialize with the service's options, not the global MVC
        // JsonStringEnumConverter: Fido2NetLib option types carry per-enum
        // converters for spec values ("public-key", "discouraged", COSE
        // algorithm numbers). The global converter would emit C# enum names
        // ("PublicKey", "Discouraged"), which browsers reject — on Android
        // the passkey bridge fails with NotSupportedError.
        return new JsonResult(await webauthn.BeginLoginAsync(ct), WebAuthnService.JsonOptions);
    }

    [HttpPost("passkeys/login/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginVerify([FromBody] JsonElement body, CancellationToken ct)
    {
        if (!AcquireAuthBudget(out var retryAfter))
            return TooManyRequests(retryAfter);
        var sessionId = await webauthn.CompleteLoginAsync(body.GetRawText(), Origin(), ct);
        AppendSessionCookie(sessionId);
        return Ok(new { ok = true });
    }

    [HttpPost("passkeys/register/options")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterOptions([FromBody] PasskeyRegisterRequest? body, CancellationToken ct)
    {
        if (!AcquireAuthBudget(out var retryAfter))
            return TooManyRequests(retryAfter);
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        // Same spec-cased serialization as login/options (see above).
        return new JsonResult(await webauthn.BeginRegistrationAsync(body?.Name, body?.InviteToken,
            CurrentUserId(), remoteIp, ct), WebAuthnService.JsonOptions);
    }

    [HttpPost("passkeys/register/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterVerify([FromBody] JsonElement body, CancellationToken ct)
    {
        if (!AcquireAuthBudget(out var retryAfter))
            return TooManyRequests(retryAfter);
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await webauthn.CompleteRegistrationAsync(body.GetRawText(), Origin(),
            CurrentUserId(), remoteIp, ct);
        if (result.SessionId is not null)
            AppendSessionCookie(result.SessionId);
        return Ok(new { id = result.PasskeyId });
    }

    /// <summary>Invite landing-page check: valid/invalid/expired/consumed —
    /// never consumes, never reveals the token (docs/MULTI-USER.md §7).</summary>
    [HttpPost("invitations/inspect")]
    [AllowAnonymous]
    public async Task<IActionResult> InspectInvitation(
        [FromBody] InviteInspectRequest input, CancellationToken ct)
    {
        if (!AcquireAuthBudget(out var retryAfter))
            return TooManyRequests(retryAfter);
        var result = await users.InspectInvitationAsync(input.Token, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(SessionCookie, out var sessionId))
        {
            await sessions.RevokeAsync(sessionId, ct);
            SessionCookies.Delete(Response);
        }
        return NoContent();
    }

    [HttpGet("passkeys")]
    public async Task<ActionResult<List<PasskeyDto>>> ListPasskeys(CancellationToken ct)
    {
        var passkeys = await webauthn.ListPasskeysForUserAsync(CurrentUserId(), ct);
        return passkeys.Select(p => new PasskeyDto
        {
            Id = p.Id, Name = p.Name, Created = p.Created, LastUsed = p.LastUsed,
        }).ToList();
    }

    [HttpDelete("passkeys/{id}")]
    public async Task<IActionResult> RemovePasskey(string id, [FromQuery] bool confirm, CancellationToken ct)
    {
        if (!confirm)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["confirm"] = ["Passkey removal requires confirm=true."],
            });
        await users.RemovePasskeyAsync(id, CurrentUserId(), ct);
        return NoContent();
    }

    private string? CurrentUserId() => HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private bool AcquireAuthBudget(out int retryAfter)
    {
        var result = rateLimiter.AcquireAuth(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", clock.UtcNow);
        retryAfter = (int)Math.Ceiling(result.RetryAfter.TotalSeconds);
        return result.Allowed;
    }

    private IActionResult TooManyRequests(int retryAfter)
    {
        Response.Headers.RetryAfter = retryAfter.ToString();
        return StatusCode(429, new ApiProblem
        {
            Type = "https://hyveman.example/errors/too_many_requests",
            Title = "Too many requests",
            Status = 429,
            Code = "too_many_requests",
            Detail = "Rate limit exceeded; retry later.",
            TraceId = HttpContext.TraceIdentifier,
        });
    }

    private void AppendSessionCookie(string sessionId)
    {
        SessionCookies.Append(Response, sessionId, sessionOptions.CurrentValue.Lifetime, Request.IsHttps);
    }

    private string Origin()
    {
        var origin = Request.Headers.Origin.ToString();
        if (origin.Length > 0) return origin;
        return $"{Request.Scheme}://{Request.Host}";
    }
}
