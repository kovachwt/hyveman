using Hyveman.Server.Config;

namespace Hyveman.Server.RateLimit;

/// <summary>
/// Rate limiting on /api/auth/* (SERVER §12.2 — "cheap insurance"; passkey auth is
/// challenge-response with no guessable code space). Bucketed per remote IP with a small
/// request budget; over-budget → 429 + Retry-After.
/// </summary>
public sealed class AuthRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimiter _limiter;
    private readonly ServerOptions.RateLimitConfig _cfg;

    public AuthRateLimitMiddleware(RequestDelegate next, RateLimiter limiter, ServerOptions opts)
    {
        _next = next;
        _limiter = limiter;
        _cfg = new ServerOptions.RateLimitConfig
        {
            RequestsPerMin = opts.Web.AuthRequestsPerMin,
            // Bytes are irrelevant for the small auth ceremony payloads; keep a generous cap.
            BytesPerMin = 4 * 1024 * 1024,
        };
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // Per-IP budget only — the ingest global budget is for agents (PROTOCOL §15); an agent
        // flood must not be able to lock out the operator's passkey login.
        var (allowed, retryAfter, remaining) = _limiter.TryTake("auth:" + ip, _cfg, 0, useGlobal: false);
        if (!allowed)
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["Retry-After"] = retryAfter.ToString();
            ctx.Response.Headers["X-RateLimit-Remaining"] = "0";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = new { code = "too_many_requests", message = "auth rate limit exceeded — retry shortly" },
            });
            return;
        }
        ctx.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();
        await _next(ctx);
    }
}
