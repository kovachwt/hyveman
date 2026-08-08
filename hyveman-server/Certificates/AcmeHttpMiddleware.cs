using Hyveman.Server.Config;

namespace Hyveman.Server.Certificates;

/// <summary>
/// Serves the http-01 ACME challenge endpoint and redirects plain-HTTP traffic to HTTPS.
///
/// Registered app-wide but only reachable in practice on the plain-HTTP listener that
/// <see cref="Program"/> adds on <c>tls.lets_encrypt.http_port</c>:
/// <list type="bullet">
/// <item><c>GET /.well-known/acme-challenge/&lt;token&gt;</c> → the pending key authorization
/// (200) or 404 when unknown — required by Let's Encrypt's http-01 validation.</item>
/// <item>any other request with <c>http</c> scheme → 308 to the same URL on the https port
/// from <c>urls</c> (so agents/operators hitting port 80 land on the TLS listener).</item>
/// <item>everything else passes through untouched.</item>
/// </list>
/// </summary>
public sealed class AcmeHttpMiddleware
{
    private const string ChallengePrefix = "/.well-known/acme-challenge/";
    private const int DefaultHttpsPort = 443;

    private readonly RequestDelegate _next;
    private readonly Http01ChallengeStore _challenges;
    private readonly int _httpsPort;

    public AcmeHttpMiddleware(RequestDelegate next, Http01ChallengeStore challenges, ServerOptions options)
    {
        _next = next;
        _challenges = challenges;
        _httpsPort = FirstHttpsPort(options.Urls);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (path.StartsWith(ChallengePrefix, StringComparison.Ordinal))
        {
            var token = path[ChallengePrefix.Length..];
            if (token.Length is > 0 and <= 256 && !token.Contains('/')
                && _challenges.TryGet(token, out var keyAuthorization))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(keyAuthorization, context.RequestAborted);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
            return;
        }

        if (!context.Request.IsHttps)
        {
            // Only the ACME challenge is ever served over plain HTTP; everything else moves to TLS.
            var host = _httpsPort == DefaultHttpsPort
                ? context.Request.Host.Host
                : $"{context.Request.Host.Host}:{_httpsPort}";
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = $"https://{host}{context.Request.Path}{context.Request.QueryString}";
            return;
        }

        await _next(context);
    }

    private static int FirstHttpsPort(string urls)
    {
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u)
                && string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return u.Port;
        }
        return DefaultHttpsPort;
    }
}
