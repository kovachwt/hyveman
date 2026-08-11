using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Hyveman.Api;
using Hyveman.Application;
using Hyveman.Infrastructure.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Hyveman.Tests.Api;

/// <summary>
/// Multi-user auth tests (docs/MULTI-USER.md): per-user passkeys, invite
/// links, user lifecycle guards, real-actor audit, and the users-based
/// first-run setup gate. WebAuthn attestation itself is exercised by the
/// browser suites; these tests cover every gate around the ceremonies.
/// </summary>
[Collection("api")]
public class MultiUserTests
{
    private readonly ApiFixture _fx;

    /// <summary>CSRF token per client: the API issues the cookie only when it
    /// is absent, so later responses carry no Set-Cookie to read it from.</summary>
    private readonly Dictionary<HttpClient, string> _csrfCache = new();

    public MultiUserTests(ApiFixture fx) => _fx = fx;

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>The shared fixture DB persists across tests in the collection,
    /// so user names must be unique per test run.</summary>
    private static string UniqueName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..6];

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        string json = "{}")
    {
        // GET /auth/session issues the CSRF cookie; the header+cookie pair is
        // required for unsafe methods (API.md §8.2).
        if (!_csrfCache.TryGetValue(client, out var csrf))
        {
            csrf = _fx.GetCsrfToken(await client.GetAsync("/api/v1/auth/session")) ?? "";
            _csrfCache[client] = csrf;
        }
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-CSRF-Token", csrf);
        req.Headers.Add("Origin", "http://localhost:5173");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(req);
    }

    // ── first-run setup gate (fresh factory, own data dir) ─────────────────

    [Fact]
    public async Task SetupGate_OpenOnFreshInstall_RequiresTrustedNetwork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hyveman-mu-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(dir);
        var previous = Environment.GetEnvironmentVariable("HYVEMAN_DATA_DIR");
        Environment.SetEnvironmentVariable("HYVEMAN_DATA_DIR", dir);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            // Fresh install: no users ⇒ setup is required.
            var session = await client.GetAsync("/api/v1/auth/session");
            Assert.True((await ReadJson(session)).GetProperty("setupRequired").GetBoolean());

            using var scope = factory.Services.CreateScope();
            var webauthn = scope.ServiceProvider.GetRequiredService<IWebAuthnService>();
            var ct = CancellationToken.None;

            // Untrusted network rejected at options.
            await Assert.ThrowsAsync<ValidationProblemException>(() =>
                webauthn.BeginRegistrationAsync(null, null, null, "203.0.113.5", ct));

            // Trusted network (loopback) accepted.
            var options = await webauthn.BeginRegistrationAsync("laptop", null, null, "127.0.0.1", ct);
            Assert.NotNull(options);

            // The aborted ceremony left no user behind: the gate stays open.
            session = await client.GetAsync("/api/v1/auth/session");
            Assert.True((await ReadJson(session)).GetProperty("setupRequired").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYVEMAN_DATA_DIR", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SetupGate_Closed_OnceAUserExists()
    {
        await _fx.SeedUserAsync(UniqueName("gate"));
        using var scope = _fx.Factory.Services.CreateScope();
        var webauthn = scope.ServiceProvider.GetRequiredService<IWebAuthnService>();
        Assert.False(await webauthn.IsSetupRequiredAsync(CancellationToken.None));

        // With users present and no invite, unauthenticated registration is
        // rejected even from the trusted network.
        await Assert.ThrowsAsync<ValidationProblemException>(() =>
            webauthn.BeginRegistrationAsync(null, null, null, "127.0.0.1", CancellationToken.None));
    }

    // ── invitations ─────────────────────────────────────────────────────────

    [Fact]
    public async Task InviteCeremony_Gates_Invalid_Consumed_Revoked_Expired()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var webauthn = scope.ServiceProvider.GetRequiredService<IWebAuthnService>();
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;

        // Unknown token.
        await Assert.ThrowsAsync<ValidationProblemException>(() =>
            webauthn.BeginRegistrationAsync(null, "inv_bogus", null, "127.0.0.1", ct));

        // Valid invite → options issued.
        var (token, id) = await _fx.CreateInvitationAsync();
        Assert.NotNull(await webauthn.BeginRegistrationAsync(null, token, null, "127.0.0.1", ct));

        // Consumed invite → rejected at options.
        using (var s2 = _fx.Factory.Services.CreateScope())
        {
            var invites = s2.ServiceProvider.GetRequiredService<IInvitationStore>();
            await invites.MarkConsumedAsync(id, now, ct);
        }
        await Assert.ThrowsAsync<ValidationProblemException>(() =>
            webauthn.BeginRegistrationAsync(null, token, null, "127.0.0.1", ct));

        // Revoked invite → rejected.
        var (token2, id2) = await _fx.CreateInvitationAsync();
        using (var s3 = _fx.Factory.Services.CreateScope())
        {
            var invites = s3.ServiceProvider.GetRequiredService<IInvitationStore>();
            await invites.RevokeAsync(id2, ct);
        }
        await Assert.ThrowsAsync<ValidationProblemException>(() =>
            webauthn.BeginRegistrationAsync(null, token2, null, "127.0.0.1", ct));

        // Expired invite → rejected.
        var (token3, id3) = await _fx.CreateInvitationAsync();
        using (var s4 = _fx.Factory.Services.CreateScope())
        {
            using var conn = s4.ServiceProvider.GetRequiredService<SqliteDb>().Open();
            await conn.ExecuteAsync(
                "UPDATE invitations SET expires_at = '2020-01-01T00:00:00.0000000Z' WHERE id = @id",
                new { id = id3 });
        }
        await Assert.ThrowsAsync<ValidationProblemException>(() =>
            webauthn.BeginRegistrationAsync(null, token3, null, "127.0.0.1", ct));
    }

    [Fact]
    public async Task InvitationsApi_Create_List_Revoke_Inspect()
    {
        var aliceName = UniqueName("alice");
        var alice = await _fx.SeedUserAsync(aliceName);
        var client = _fx.NewClient();
        _fx.SeedSessionAs(client, alice);

        var create = await SendAsync(client, HttpMethod.Post, "/api/v1/users/invitations",
            """{"expiresInMinutes":1440}""");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await ReadJson(create);
        var token = body.GetProperty("token").GetString()!;
        var inviteId = body.GetProperty("id").GetString()!;
        Assert.StartsWith("inv_", token);
        Assert.Contains("#token=", body.GetProperty("url").GetString());

        // List shows metadata, never the token.
        var list = await client.GetAsync("/api/v1/users/invitations");
        var arr = (await ReadJson(list)).EnumerateArray().ToList();
        Assert.Contains(arr, i => i.GetProperty("createdByDisplayName").GetString() == aliceName);
        Assert.DoesNotContain(arr, i => i.TryGetProperty("token", out _));

        // Inspect (unauthenticated-able) reports valid without consuming.
        var inspect = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/invitations/inspect",
            $"{{\"token\":\"{token}\"}}");
        Assert.True((await ReadJson(inspect)).GetProperty("valid").GetBoolean());

        // Revoke → inspect reports invalid.
        var revoke = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/invitations/{inviteId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var inspect2 = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/invitations/inspect",
            $"{{\"token\":\"{token}\"}}");
        Assert.False((await ReadJson(inspect2)).GetProperty("valid").GetBoolean());

        // Unknown token → 404.
        var inspect3 = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/invitations/inspect",
            """{"token":"inv_nope"}""");
        Assert.Equal(HttpStatusCode.NotFound, inspect3.StatusCode);
    }

    // ── user lifecycle ──────────────────────────────────────────────────────

    [Fact]
    public async Task UsersApi_Lifecycle_Guards_And_RealActorAudit()
    {
        var aliceName = UniqueName("alice");
        var bobName = UniqueName("bob");
        var alice = await _fx.SeedUserAsync(aliceName);
        var bob = await _fx.SeedUserAsync(bobName);
        var client = _fx.NewClient();
        _fx.SeedSessionAs(client, alice);

        // Bob has a live session before being disabled.
        var bobClient = _fx.NewClient();
        _fx.SeedSessionAs(bobClient, bob);
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync("/api/v1/overview")).StatusCode);

        // List (through SendAsync so the CSRF cookie capture is deterministic).
        var list = await SendAsync(client, HttpMethod.Get, "/api/v1/users");
        var names = (await ReadJson(list)).EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains(aliceName, names);
        Assert.Contains(bobName, names);

        // Disable bob → 204; bob's session dies immediately.
        var disable = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{bob}/disable");
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bobClient.GetAsync("/api/v1/overview")).StatusCode);

        // Disabling yourself → 400.
        var self = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{alice}/disable");
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        // Enable bob again.
        var enable = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{bob}/enable");
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);

        // Delete bob; delete self is blocked.
        var del = await SendAsync(client, HttpMethod.Delete, $"/api/v1/users/{bob}?confirm=true");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var delSelf = await SendAsync(client, HttpMethod.Delete, $"/api/v1/users/{alice}?confirm=true");
        Assert.Equal(HttpStatusCode.BadRequest, delSelf.StatusCode);

        // Audit carries the acting user's real name.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
            var entries = await audit.ListAsync(new AuditQuery(null, null, null, null, 100, null), CancellationToken.None);
            Assert.Contains(entries, e => e.Action == "user.disabled" && e.Actor == aliceName && e.TargetId == bob);
            Assert.Contains(entries, e => e.Action == "user.deleted" && e.Actor == aliceName && e.TargetId == bob);
        }
    }

    [Fact]
    public async Task Users_Delete_Cascades_PasskeysAndSessions()
    {
        var alice = await _fx.SeedUserAsync(UniqueName("alice"));
        var pk = await _fx.SeedPasskeyAsync(alice, "alice-key");
        var admin2 = await _fx.SeedUserAsync(UniqueName("admin"));
        var client = _fx.NewClient();
        _fx.SeedSessionAs(client, admin2);

        // Seed a session for alice, then delete her.
        var aliceClient = _fx.NewClient();
        _fx.SeedSessionAs(aliceClient, alice);
        var del = await SendAsync(client, HttpMethod.Delete, $"/api/v1/users/{alice}?confirm=true");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var passkeys = scope.ServiceProvider.GetRequiredService<IPasskeyStore>();
        Assert.Null(await passkeys.GetAsync(pk, CancellationToken.None));
        using var conn = scope.ServiceProvider.GetRequiredService<SqliteDb>().Open();
        var sessions = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM web_sessions WHERE user_id = @u", new { u = alice });
        Assert.Equal(0, sessions);
    }

    [Fact]
    public async Task SelfDisable_And_SelfDelete_AreBlocked()
    {
        var alice = await _fx.SeedUserAsync(UniqueName("alice"));
        var client = _fx.NewClient();
        _fx.SeedSessionAs(client, alice);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{alice}/disable")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Delete, $"/api/v1/users/{alice}?confirm=true")).StatusCode);
    }

    [Fact]
    public async Task Passkeys_List_ScopedTo_SessionUser()
    {
        var alice = await _fx.SeedUserAsync(UniqueName("alice"));
        var bob = await _fx.SeedUserAsync(UniqueName("bob"));
        var pkA = await _fx.SeedPasskeyAsync(alice, "alice-key");
        await _fx.SeedPasskeyAsync(bob, "bob-key");

        var client = _fx.NewClient();
        _fx.SeedSessionAs(client, alice);
        var resp = await client.GetAsync("/api/v1/auth/passkeys");
        var ids = (await ReadJson(resp)).EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal([pkA], ids);
    }

    [Fact]
    public async Task PasskeyRemoval_Guards_SelfLockout_And_FleetLockout()
    {
        var alice = await _fx.SeedUserAsync(UniqueName("alice"));
        var pkA = await _fx.SeedPasskeyAsync(alice);
        var bob = await _fx.SeedUserAsync(UniqueName("bob"));
        var pkB = await _fx.SeedPasskeyAsync(bob);

        using var scope = _fx.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UsersService>();
        var ct = CancellationToken.None;

        // Alice cannot strip her own last passkey even though bob remains.
        await Assert.ThrowsAsync<ValidationProblemException>(() => users.RemovePasskeyAsync(pkA, alice, ct));

        // Bob (equal permissions) may remove alice's passkey.
        await users.RemovePasskeyAsync(pkA, bob, ct);

        // alice's passkey was the only other login path; now bob's key is the
        // sole login path — nobody may remove it.
        await Assert.ThrowsAsync<ValidationProblemException>(() => users.RemovePasskeyAsync(pkB, alice, ct));
        await Assert.ThrowsAsync<ValidationProblemException>(() => users.RemovePasskeyAsync(pkB, bob, ct));
    }

    // ── session & audit ─────────────────────────────────────────────────────

    [Fact]
    public async Task Session_Reports_AuthenticatedUser()
    {
        var aliceName = UniqueName("alice");
        var alice = await _fx.SeedUserAsync(aliceName, "Alice Example");
        var client = _fx.NewClient();
        _fx.SeedSessionAs(client, alice);
        var json = await ReadJson(await client.GetAsync("/api/v1/auth/session"));
        Assert.True(json.GetProperty("authenticated").GetBoolean());
        Assert.False(json.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(alice, json.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(aliceName, json.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal("Alice Example", json.GetProperty("user").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Mutations_AreAudited_WithRealUsername()
    {
        var aliceName = UniqueName("alice");
        var alice = await _fx.SeedUserAsync(aliceName);
        var client = _fx.NewClient();
        _fx.SeedSessionAs(client, alice);

        var create = await SendAsync(client, HttpMethod.Post, "/api/v1/hosts",
            """{"name":"HOST-MU-1","kind":"windows-server"}""");
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var entries = await audit.ListAsync(new AuditQuery("host.created", null, null, null, 10, null),
            CancellationToken.None);
        Assert.Contains(entries, e => e.Actor == aliceName);
    }
}
