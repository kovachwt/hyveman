using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Hyveman.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hyveman.Api;

/// <summary>Session cookie authentication for the web API (API.md §8.2):
/// opaque HttpOnly session id, server-side revocable record, 14-day sliding
/// expiry handled by ISessionStore. Agent bearer tokens are never accepted
/// on web routes.</summary>
public sealed class SessionAuthOptions : AuthenticationSchemeOptions
{
    public const string CookieName = "hyveman_session";
    public const string SchemeName = "Session";
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(14);
}

public sealed class SessionAuthHandler(
    IOptionsMonitor<SessionAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISessionStore sessions,
    IClock clock) : AuthenticationHandler<SessionAuthOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionAuthOptions.CookieName, out var sessionId) ||
            string.IsNullOrEmpty(sessionId))
            return AuthenticateResult.NoResult();

        var lifetime = options.CurrentValue.Lifetime;
        var session = await sessions.ValidateAsync(sessionId, clock.UtcNow, lifetime, Context.RequestAborted);
        if (session is null)
        {
            SessionCookies.Delete(Response);
            return AuthenticateResult.Fail("invalid or expired session");
        }

        // Sliding cookie (API.md §8.2): re-issue on each successful slide so
        // the browser cookie tracks the server record instead of expiring 14
        // days after login regardless of activity (D6).
        SessionCookies.Append(Response, sessionId, lifetime, Request.IsHttps);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim("session_id_hash", session.IdHash)],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>Shared session-cookie issuance (API.md §8.2): HttpOnly, Secure,
/// SameSite=Strict, Path=/, with a MaxAge equal to the configured session
/// lifetime. Used both at login (AuthController) and on every successful
/// validation (SessionAuthHandler) so the cookie slides in lockstep with the
/// server-side record.</summary>
public static class SessionCookies
{
    public static void Append(HttpResponse response, string sessionId, TimeSpan lifetime, bool isHttps)
    {
        response.Cookies.Append(SessionAuthOptions.CookieName, sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = lifetime,
        });
    }

    public static void Delete(HttpResponse response) =>
        response.Cookies.Delete(SessionAuthOptions.CookieName);
}

/// <summary>CSRF + Origin enforcement for unsafe web requests (API.md §5.2/§8.2):
/// an allowed Origin (or Referer), plus an anti-CSRF token supplied in a header
/// and cookie pair. The csrf cookie is issued by /api/v1/auth/session (and any
/// response here) so the browser always has it before mutating.</summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string CookieName = "hyveman_csrf";
    public const string HeaderName = "X-CSRF-Token";

    private static readonly HashSet<string> UnsafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete,
    };

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/v1/", StringComparison.Ordinal))
        {
            var opts = ctx.RequestServices.GetRequiredService<HyvemanOptions>();

            // Always ensure a csrf cookie exists (GETs included) so mutation
            // clients have the pair.
            if (!ctx.Request.Cookies.TryGetValue(CookieName, out var csrf) || csrf.Length < 16)
            {
                csrf = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                ctx.Response.Cookies.Append(CookieName, csrf, new CookieOptions
                {
                    Path = "/",
                    HttpOnly = false,       // read by the frontend to send as header
                    Secure = ctx.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    IsEssential = true,
                });
            }

            if (UnsafeMethods.Contains(ctx.Request.Method))
            {
                // Origin/Referer must be allowed when present.
                var origin = ctx.Request.Headers.Origin.ToString();
                var referer = ctx.Request.Headers.Referer.ToString();
                if (origin.Length > 0 && !IsAllowedOrigin(origin, opts))
                {
                    await WriteProblem(ctx, 403, "origin_not_allowed", "Origin is not allowed.");
                    return;
                }
                if (origin.Length == 0 && referer.Length > 0 &&
                    Uri.TryCreate(referer, UriKind.Absolute, out var refUri) &&
                    !IsAllowedOrigin(refUri.GetLeftPart(UriPartial.Authority), opts))
                {
                    await WriteProblem(ctx, 403, "origin_not_allowed", "Referer is not allowed.");
                    return;
                }

                // Header+cookie CSRF pair.
                var header = ctx.Request.Headers[HeaderName].ToString();
                if (header.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(header),
                        System.Text.Encoding.UTF8.GetBytes(csrf)))
                {
                    await WriteProblem(ctx, 403, "csrf_mismatch", "Missing or mismatched CSRF token.");
                    return;
                }
            }
        }

        await next(ctx);
    }

    private static bool IsAllowedOrigin(string origin, HyvemanOptions opts)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(opts.WebAuthnExpectedOrigin)) candidates.Add(opts.WebAuthnExpectedOrigin);
        if (!string.IsNullOrEmpty(opts.PublicOrigin)) candidates.Add(opts.PublicOrigin);
        candidates.AddRange(opts.AllowedOrigins);
        // Dev convenience: any localhost origin is accepted (setup wizard runs
        // from a static dev server on any port).
        if (candidates.Count == 0 || candidates.Any(c => c.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var o) &&
                (o.Host == "localhost" || o.Host == "127.0.0.1" || o.Host == "::1"))
                return true;
        }
        return candidates.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task WriteProblem(HttpContext ctx, int status, string code, string detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsJsonAsync(new Hyveman.Contracts.ApiProblem
        {
            Type = $"https://hyveman.example/errors/{code}",
            Title = "Request rejected",
            Status = status,
            Code = code,
            Detail = detail,
            TraceId = ctx.TraceIdentifier,
        }, ctx.RequestAborted);
    }
}

/// <summary>Maps application exceptions to RFC 9457 Problem Details (API.md §5.2).</summary>
public sealed class ProblemDetailsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (NotFoundException ex)
        {
            await WriteAsync(ctx, 404, "not_found", "Not found", ex.Message);
        }
        catch (ConflictException ex)
        {
            await WriteAsync(ctx, 409, "conflict", "Concurrent modification", ex.Message);
        }
        catch (ValidationProblemException ex)
        {
            await WriteAsync(ctx, 400, "validation_failed", "Validation failed",
                "One or more fields are invalid.", ex.Errors);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteAsync(ctx, 401, "unauthorized", "Unauthorized", "Authentication required.");
        }
        catch (Exception ex)
        {
            var log = ctx.RequestServices.GetRequiredService<ILogger<ProblemDetailsMiddleware>>();
            log.LogError(ex, "Unhandled error on {path}", ctx.Request.Path);
            await WriteAsync(ctx, 500, "internal", "Internal error", "An internal error occurred.");
        }
    }

    private static async Task WriteAsync(HttpContext ctx, int status, string code, string title,
        string detail, Dictionary<string, List<string>>? errors = null)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsJsonAsync(new Hyveman.Contracts.ApiProblem
        {
            Type = $"https://hyveman.example/errors/{code}",
            Title = title,
            Status = status,
            Code = code,
            Detail = detail,
            TraceId = ctx.TraceIdentifier,
            Errors = errors,
        }, ctx.RequestAborted);
    }
}
