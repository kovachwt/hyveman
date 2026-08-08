using System.Security.Cryptography;
using System.Text;
using Hyveman.Server.Auth;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Tests.TestInfra;
using Microsoft.AspNetCore.Http;

namespace Hyveman.Server.Tests.Auth;

/// <summary>
/// Session cookie semantics (§8 / §12.2): HttpOnly/Secure/SameSite=Strict issuance,
/// HMAC tamper detection, expiry, and passkey binding.
/// </summary>
public sealed class SessionAuthTests
{
    private static async Task<(SessionAuth sessions, string credId)> SetupAsync(TestDb db)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var credId = "cred_test_1";
        await db.Db.Writer.WithTransactionAsync(async conn =>
            await Hyveman.Server.Storage.Repos.PasskeyRepository.InsertAsync(conn, "pk_1", "test passkey", credId, RandomNumberGenerator.GetBytes(65)));
        var opts = new ServerOptions();
        return (new SessionAuth(key, db.Db, opts), credId);
    }

    private static DefaultHttpContext ContextWithCookie(string cookie)
    {
        var ctx = new DefaultHttpContext();
        if (cookie is not null)
            ctx.Request.Headers["Cookie"] = $"{SessionAuth.CookieName}={cookie}";
        return ctx;
    }

    private static string Sign(byte[] serverKey, string payload)
    {
        var hmacKey = SessionCrypto.DeriveKey(serverKey, "hyveman-session-hmac");
        using var hmac = new HMACSHA256(hmacKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    [Fact]
    public async Task IssueAndValidate_RoundTrips()
    {
        using var db = await TestDb.CreateAsync();
        var (sessions, credId) = await SetupAsync(db);

        var ctx = new DefaultHttpContext();
        await db.Db.Passkeys.GetByCredentialIdAsync(credId); // sanity
        // Get a passkey row to issue against.
        var passkey = await db.Db.Passkeys.GetByCredentialIdAsync(credId);
        sessions.Issue(ctx, passkey!);

        var cookie = ctx.Response.Headers.SetCookie.FirstOrDefault()!;
        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", cookie, StringComparison.OrdinalIgnoreCase);

        // Feed the issued cookie back in on a fresh context.
        var value = cookie.Split(';')[0].Split('=')[1];
        var (ok, pk) = await sessions.ValidateAsync(ContextWithCookie(value));

        Assert.True(ok);
        Assert.Equal(credId, pk!.CredentialId);
    }

    [Fact]
    public async Task TamperedSignature_IsRejected()
    {
        using var db = await TestDb.CreateAsync();
        var (sessions, credId) = await SetupAsync(db);

        var expiry = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds().ToString();
        var payload = $"{credId}.{expiry}";
        var cookie = $"{payload}.{Sign(RandomNumberGenerator.GetBytes(32), payload).Replace('a', 'b')}"; // wrong key + flip

        var (ok, _) = await sessions.ValidateAsync(ContextWithCookie(cookie));
        Assert.False(ok);
    }

    [Fact]
    public async Task ExpiredCookie_IsRejectedAndRemoved()
    {
        using var db = await TestDb.CreateAsync();
        var (sessions, credId) = await SetupAsync(db);
        var serverKey = RandomNumberGenerator.GetBytes(32);
        var sessionsB = new SessionAuth(serverKey, db.Db, new ServerOptions());

        // Correctly signed but already-expired cookie.
        var expiry = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds().ToString();
        var payload = $"{credId}.{expiry}";
        var cookie = $"{payload}.{Sign(serverKey, payload)}";

        var ctx = ContextWithCookie(cookie);
        var (ok, _) = await sessionsB.ValidateAsync(ctx);
        Assert.False(ok);
        Assert.Contains(SessionAuth.CookieName, ctx.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task UnknownPasskey_IsRejected()
    {
        using var db = await TestDb.CreateAsync();
        var serverKey = RandomNumberGenerator.GetBytes(32);
        var sessions = new SessionAuth(serverKey, db.Db, new ServerOptions());

        var expiry = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds().ToString();
        var payload = $"cred_nonexistent.{expiry}";
        var cookie = $"{payload}.{Sign(serverKey, payload)}";

        var (ok, _) = await sessions.ValidateAsync(ContextWithCookie(cookie));
        Assert.False(ok);
    }
}
