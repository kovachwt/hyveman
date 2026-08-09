using System.Text.Json;
using Hyveman.Application;
using Hyveman.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hyveman.Api;

/// <summary>Passkey-only web authentication (API.md §8.1): the API owns all
/// ceremony state; the browser receives only the options and posts the
/// credential response back. Session cookie issued on successful verify.</summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    IWebAuthnService webauthn,
    ISessionStore sessions,
    IClock clock,
    RateLimiterRegistry rateLimiter,
    ILogger<AuthController> log) : ControllerBase
{
    private const string SessionCookie = SessionAuthOptions.CookieName;

    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<ActionResult<SessionResponse>> Session(CancellationToken ct)
    {
        var authenticated = HttpContext.User.Identity?.IsAuthenticated == true;
        var setupRequired = await webauthn.IsSetupRequiredAsync(ct);
        return new SessionResponse
        {
            Authenticated = authenticated,
            SetupRequired = setupRequired && !authenticated,
            AdminName = authenticated ? "admin" : null,
        };
    }

    [HttpPost("passkeys/login/options")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginOptions(CancellationToken ct)
    {
        if (!AcquireAuthBudget(out var retryAfter))
            return TooManyRequests(retryAfter);
        return Ok(await webauthn.BeginLoginAsync(ct));
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
        var authenticated = HttpContext.User.Identity?.IsAuthenticated == true;
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        return Ok(await webauthn.BeginRegistrationAsync(body?.Name, authenticated, remoteIp, ct));
    }

    [HttpPost("passkeys/register/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterVerify([FromBody] JsonElement body, CancellationToken ct)
    {
        if (!AcquireAuthBudget(out var retryAfter))
            return TooManyRequests(retryAfter);
        var passkeyId = await webauthn.CompleteRegistrationAsync(body.GetRawText(), Origin(), ct);
        return Ok(new { id = passkeyId });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(SessionCookie, out var sessionId))
        {
            await sessions.RevokeAsync(sessionId, ct);
            Response.Cookies.Delete(SessionCookie);
        }
        return NoContent();
    }

    [HttpGet("passkeys")]
    public async Task<ActionResult<List<PasskeyDto>>> ListPasskeys(CancellationToken ct)
    {
        var passkeys = await webauthn.ListPasskeysAsync(ct);
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
        await webauthn.RemovePasskeyAsync(id, ct);
        return NoContent();
    }

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
        Response.Cookies.Append(SessionCookie, sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(14),
        });
    }

    private string Origin()
    {
        var origin = Request.Headers.Origin.ToString();
        if (origin.Length > 0) return origin;
        return $"{Request.Scheme}://{Request.Host}";
    }
}
