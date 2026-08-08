using System.Security.Cryptography;
using System.Text;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Storage;

namespace Hyveman.Server.Auth;

/// <summary>
/// Passkey session cookie: HttpOnly; Secure; SameSite=Strict, 14-day sliding expiry (§12.2).
/// The cookie holds base64url(credential_id)‖expiry‖HMAC(key=K‖"session"), so it is
/// tamper-proof and survives restarts. Each authenticated request extends the expiry.
/// </summary>
public sealed class SessionAuth
{
    public const string CookieName = "hyveman_session";
    private readonly byte[] _hmacKey;
    private readonly Db _db;
    private readonly int _sessionDays;

    public SessionAuth(byte[] serverKey, Db db, ServerOptions opts)
    {
        _hmacKey = SessionCrypto.DeriveKey(serverKey, "hyveman-session-hmac");
        _db = db;
        _sessionDays = opts.Web.SessionDays;
    }

    public async Task<(bool ok, Storage.Repos.PasskeyRow? passkey)> ValidateAsync(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var cookie) || string.IsNullOrEmpty(cookie))
            return (false, null);
        var parts = cookie.Split('.');
        if (parts.Length != 3) return (false, null);

        var credId = parts[0];
        var expiry = parts[1];
        var sig = parts[2];

        var expected = Sign(credId + "." + expiry);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(sig)))
            return (false, null);

        if (!long.TryParse(expiry, out var expMs) || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > expMs)
        {
            Remove(ctx);
            return (false, null);
        }

        var passkey = await _db.Passkeys.GetByCredentialIdAsync(credId);
        if (passkey is null)
        {
            Remove(ctx);
            return (false, null);
        }

        // Sliding expiry: refresh the cookie on authenticated requests.
        Issue(ctx, passkey);
        return (true, passkey);
    }

    public void Issue(HttpContext ctx, Storage.Repos.PasskeyRow passkey)
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(_sessionDays).ToUnixTimeMilliseconds();
        var payload = $"{passkey.CredentialId}.{expiry}";
        var cookie = payload + "." + Sign(payload);
        ctx.Response.Cookies.Append(CookieName, cookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.FromUnixTimeMilliseconds(expiry),
            Path = "/",
        });
    }

    public void Remove(HttpContext ctx)
        => ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(_hmacKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

/// <summary>
/// Blazor-side guard: requires a valid passkey session for all /(dashboard|host|search|alerts|admin)
/// routes; redirects to /auth/login. /auth/setup is served only while passkeys is empty.
/// </summary>
public sealed class PasskeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SessionAuth _sessions;
    private readonly PasskeyService _passkeys;
    private readonly ServerOptions _opts;
    private readonly ILogger<PasskeyAuthMiddleware> _logger;

    public PasskeyAuthMiddleware(RequestDelegate next, SessionAuth sessions, PasskeyService passkeys,
        ServerOptions opts, ILogger<PasskeyAuthMiddleware> logger)
    {
        _next = next;
        _sessions = sessions;
        _passkeys = passkeys;
        _opts = opts;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var isSetup = path.StartsWith("/auth/setup", StringComparison.OrdinalIgnoreCase);
        var isLogin = path.StartsWith("/auth/login", StringComparison.OrdinalIgnoreCase);
        var isStatic = path.StartsWith("/css") || path.StartsWith("/js") || path.StartsWith("/favicon")
            || path.StartsWith("/_framework") || path.StartsWith("/_blazor");
        var isIngest = path.StartsWith("/ingest/") || path == "/register" || path == "/health";
        var isAuthApi = path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase);

        if (isStatic || isIngest || isAuthApi)
        {
            await _next(ctx);
            return;
        }

        var (ok, _) = await _sessions.ValidateAsync(ctx);
        var hasPasskeys = await _passkeys.HasPasskeysAsync();

        if (isSetup)
        {
            if (hasPasskeys)
            {
                ctx.Response.StatusCode = 404;   // never served once ≥1 passkey exists (§11.3)
                await ctx.Response.WriteAsync("Not found.");
                return;
            }
            if (!IsTrustedOrigin(ctx))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync(
                    "First-run setup must be performed from localhost or a trusted network. " +
                    "Run the browser on the server itself, or configure web.trusted_networks.");
                return;
            }
            await _next(ctx);
            return;
        }

        if (isLogin)
        {
            if (ok && hasPasskeys)
            {
                ctx.Response.Redirect("/dashboard");
                return;
            }
            await _next(ctx);
            return;
        }

        // Protected pages.
        if (!hasPasskeys)
        {
            ctx.Response.Redirect("/auth/setup");
            return;
        }
        if (!ok)
        {
            ctx.Response.Redirect("/auth/login");
            return;
        }
        await _next(ctx);
    }

    private bool IsTrustedOrigin(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null) return false;
        if (System.Net.IPAddress.IsLoopback(remote)) return true;
        foreach (var cidr in _opts.Web.TrustedNetworks)
        {
            if (CidrContains(cidr, remote)) return true;
        }
        return false;
    }

    private static bool CidrContains(string cidr, System.Net.IPAddress ip)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var net) || !int.TryParse(parts[1], out var prefix))
            return false;
        if (net.AddressFamily != ip.AddressFamily) return false;
        var n = net.GetAddressBytes();
        var a = ip.GetAddressBytes();
        var bits = prefix;
        for (var i = 0; i < n.Length && bits > 0; i++)
        {
            var b = Math.Min(8, bits);
            var mask = (byte)(0xFF << (8 - b));
            if ((n[i] & mask) != (a[i] & mask)) return false;
            bits -= b;
        }
        return true;
    }
}
