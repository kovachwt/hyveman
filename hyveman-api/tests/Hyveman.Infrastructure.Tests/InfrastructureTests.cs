using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Hyveman.Application;
using Hyveman.Domain;
using Hyveman.Infrastructure.Redfish;
using Hyveman.Infrastructure.Security;
using Hyveman.Infrastructure.Sqlite;
using Xunit;

namespace Hyveman.Tests.Infrastructure;

public class SqliteIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteDb _db;

    public SqliteIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hyveman-test-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_dir);
        _db = new SqliteDb(Path.Combine(_dir, "test.db"));
        _db.Migrate();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ValidatedLogItem Item(string recordId, string scope, string message, int? severity = null, string? channel = null, long? eventId = null) => new(
        scope, recordId, DateTimeOffset.Parse("2024-08-07T15:02:11Z"), severity,
        "Microsoft-Windows-Kernel-Power", message, "{}", null, channel, eventId, 0, 0, null);

    [Fact]
    public async Task Migrations_Apply_CreateAllTables()
    {
        using var conn = _db.Open();
        var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table'")).ToList();
        foreach (var expected in new[]
        {
            "sources", "hosts", "tokens", "registration_tokens", "events", "events_fts",
            "agent_status", "vms", "components", "health_snapshots", "metrics", "alerts",
            "rules", "rule_channels", "notification_channels", "notification_outbox",
            "passkeys", "credentials", "maintenance_windows", "audit_log", "web_sessions",
            "webauthn_challenges", "saved_searches", "settings", "logon_stats", "poll_status",
            "idrac_trusted_certs",
        })
        {
            Assert.Contains(expected, tables);
        }
        // WAL mode + FK enforcement.
        Assert.Equal("wal", (await conn.QuerySingleAsync<string>("PRAGMA journal_mode")).ToLowerInvariant());
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("PRAGMA foreign_keys"));
    }

    [Fact]
    public async Task IdracCertStore_RoundTripsPinsPerHost()
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO hosts(id, name, kind, enabled, updated_at, created_at) " +
            "VALUES ('hst_1','HOST01','windows-server',1,'2024-01-01T00:00:00.0000000Z','2024-01-01T00:00:00.0000000Z')");

        var store = new IdracCertStore(_db);
        var der = new byte[] { 0x30, 0x82, 0x01, 0x2a, 0x01, 0x02 };
        var fp = IdracCertPolicies.FingerprintOf(der);
        var at = DateTimeOffset.Parse("2024-08-07T15:02:11Z");

        Assert.Null(await store.GetFingerprintAsync("hst_1", CancellationToken.None));
        await store.SetAsync("hst_1", der, fp, at, CancellationToken.None);

        Assert.Equal(fp, await store.GetFingerprintAsync("hst_1", CancellationToken.None));
        var pin = await store.GetPinAsync("hst_1", CancellationToken.None);
        Assert.NotNull(pin);
        Assert.Equal(fp, pin!.Fingerprint);
        Assert.Equal(der, pin.CertDer);
        Assert.Equal(at, pin.AcceptedAt);

        // Update replaces the pin (certificate rotation handled by clearing).
        var der2 = new byte[] { 0x30, 0x82, 0x01, 0x2a, 0x07, 0x07 };
        var fp2 = IdracCertPolicies.FingerprintOf(der2);
        await store.SetAsync("hst_1", der2, fp2, at.AddDays(1), CancellationToken.None);
        Assert.Equal(fp2, await store.GetFingerprintAsync("hst_1", CancellationToken.None));

        await store.DeleteAsync("hst_1", CancellationToken.None);
        Assert.Null(await store.GetFingerprintAsync("hst_1", CancellationToken.None));
    }

    [Fact]
    public async Task Events_IdempotentInsert_DedupesAndSearches()
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO sources(id, kind, name, created_at) VALUES ('src_1','windows-agent','HOST01','2024-01-01T00:00:00.0000000Z')");

        var store = new EventStore(_db);
        var first = await store.InsertBatchAsync("src_1",
            [Item("41235", "System", "unexpected shutdown", 3, "System", 6008),
             Item("41236", "System", "disk error", 2, "System", 7)], CancellationToken.None);
        Assert.Equal(2, first.Accepted);
        Assert.Equal(0, first.Deduped);

        // Replay: the unique key collapses duplicates.
        var replay = await store.InsertBatchAsync("src_1",
            [Item("41235", "System", "unexpected shutdown again", 3),
             Item("e1:1", "System", "after channel clear", 4)], CancellationToken.None);
        Assert.Equal(1, replay.Accepted);
        Assert.Equal(1, replay.Deduped);

        // FTS5 search finds the newly inserted message only.
        var page = await store.SearchAsync(new EventQuery(null, null, null, "src_1", null, null, null, "unexpected", 50, null, "desc"), CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal("unexpected shutdown", page.Items[0].Message);
        Assert.Equal(6008, page.Items[0].EventId);
        Assert.Equal("System", page.Items[0].Channel);

        // Structured filter.
        var byEvent = await store.SearchAsync(new EventQuery(null, null, null, "src_1", null, null, 6008, null, 50, null, "desc"), CancellationToken.None);
        Assert.Single(byEvent.Items);
        Assert.Equal("41235", byEvent.Items[0].RecordId);

        // Epoch-prefixed record id is distinct from the bare id.
        var epoch = await store.GetAsync(3, CancellationToken.None);
        Assert.Equal("e1:1", epoch!.RecordId);
    }

    [Fact]
    public async Task Events_CursorPagination()
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO sources(id, kind, name, created_at) VALUES ('src_1','windows-agent','HOST01','2024-01-01T00:00:00.0000000Z')");
        var store = new EventStore(_db);
        var items = Enumerable.Range(1, 10)
            .Select(i => new ValidatedLogItem("Sys", i.ToString(),
                DateTimeOffset.Parse("2024-08-07T15:00:00Z").AddMinutes(i), 4, "fac", $"message {i}",
                "{}", null, null, null, null, null, null))
            .ToList();
        await store.InsertBatchAsync("src_1", items, CancellationToken.None);

        var page1 = await store.SearchAsync(new EventQuery(null, null, null, null, null, null, null, null, 4, null, "desc"), CancellationToken.None);
        Assert.Equal(4, page1.Items.Count);
        Assert.True(page1.HasMore);
        var cursor = CursorCodec.Encode(page1.Items[^1].Time, page1.Items[^1].Id);
        var page2 = await store.SearchAsync(new EventQuery(null, null, null, null, null, null, null, null, 4, cursor, "desc"), CancellationToken.None);
        Assert.Equal(4, page2.Items.Count);
        Assert.True(page2.HasMore);
        Assert.DoesNotContain(page1.Items.Select(i => i.Id), id => page2.Items.Any(i => i.Id == id));
    }

    [Fact]
    public async Task Retention_PurgesEventsAndFts()
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO sources(id, kind, name, created_at) VALUES ('src_1','windows-agent','HOST01','2024-01-01T00:00:00.0000000Z')");
        var store = new EventStore(_db);
        await store.InsertBatchAsync("src_1",
            [Item("1", "S", "old message"), Item("2", "S", "new message")], CancellationToken.None);
        using (var c = _db.Open())
        {
            await c.ExecuteAsync("UPDATE events SET time = '2020-01-01T00:00:00.0000000Z' WHERE record_id = '1'");
        }
        var purged = await store.PurgeOlderThanAsync(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), CancellationToken.None);
        Assert.Equal(1, purged);
        // FTS rows purged with the events (external-content table kept in sync).
        var ftsCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM events_fts");
        Assert.Equal(1, ftsCount);
        var page = await store.SearchAsync(new EventQuery(null, null, null, null, null, null, null, "old", 50, null, "desc"), CancellationToken.None);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task TokenStore_HashLookup_AndRevocation()
    {
        var store = new TokenStore(_db);
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO sources(id, kind, name, created_at) VALUES ('src_1','windows-agent','HOST01','2024-01-01T00:00:00.0000000Z')");
        var raw = await store.CreateAgentTokenAsync("src_1", ["ingest"], DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.StartsWith("agt_", raw);

        var auth = await store.AuthenticateAsync(raw, CancellationToken.None);
        Assert.NotNull(auth);
        Assert.Equal("src_1", auth!.SourceId);
        Assert.Equal("windows-agent", auth.SourceKind);

        // The raw token is never stored.
        var stored = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tokens WHERE token_hash = @h",
            new { h = StoreHelpers.HashToken(raw) });
        Assert.Equal(1, stored);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tokens WHERE id != '' AND token_hash = @raw",
            new { raw }));

        // Revoked tokens no longer authenticate.
        var tokenId = auth.TokenId;
        Assert.True(await store.RevokeAsync(tokenId, CancellationToken.None));
        Assert.True(await store.IsRevokedAsync(raw, CancellationToken.None));
        Assert.Null(await store.AuthenticateAsync(raw, CancellationToken.None));
    }

    [Fact]
    public async Task AgentStatus_HeartbeatOrdering_Persisted()
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO sources(id, kind, name, created_at) VALUES ('src_1','windows-agent','HOST01','2024-01-01T00:00:00.0000000Z')");
        var store = new AgentStatusStore(_db);
        var boot = DateTimeOffset.Parse("2024-08-01T00:00:00Z");
        var hb = new HeartbeatPayload(DateTimeOffset.Parse("2024-08-07T10:00:00Z"), "0.1.0", 1, "17763", boot, 100, "", "abc", "{}", null);

        Assert.True(await store.ApplyHeartbeatAsync("src_1", hb, DateTimeOffset.Parse("2024-08-07T10:00:05Z"), CancellationToken.None));
        var row = await store.GetAsync("src_1", CancellationToken.None);
        Assert.Equal("0.1.0", row!.AgentVersion);
        Assert.Equal(DateTimeOffset.Parse("2024-08-07T10:00:05Z"), row.LastReceived);

        // Older sent_at, same boot: receive time updates, state does not.
        var older = hb with { SentAt = DateTimeOffset.Parse("2024-08-07T09:00:00Z") };
        Assert.False(await store.ApplyHeartbeatAsync("src_1", older, DateTimeOffset.Parse("2024-08-07T11:00:00Z"), CancellationToken.None));
        row = await store.GetAsync("src_1", CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), row!.LastReceived);
        Assert.Equal(DateTimeOffset.Parse("2024-08-07T10:00:00Z"), row.LastSentAt);

        // New boot session: state stored again.
        var newBoot = hb with { BootTime = DateTimeOffset.Parse("2024-08-05T00:00:00Z"), SentAt = DateTimeOffset.Parse("2024-08-07T09:30:00Z") };
        Assert.True(await store.ApplyHeartbeatAsync("src_1", newBoot, DateTimeOffset.Parse("2024-08-07T11:30:00Z"), CancellationToken.None));
        row = await store.GetAsync("src_1", CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse("2024-08-05T00:00:00Z"), row.BootTime);
    }

    [Fact]
    public async Task Vault_RoundTrip_And_NoPlaintext()
    {
        var keyPath = Path.Combine(_dir, "vault.key");
        var blobStore = new CredentialBlobStore(_db);
        var vault = new CredentialVault(blobStore, keyPath, new SystemClock());

        var id = await vault.StoreAsync(CredentialKinds.Idrac, "HOST01 iDRAC",
            """{"username":"root","password":"s3cret"}""", CancellationToken.None);
        Assert.StartsWith("crd_", id);
        Assert.Equal("""{"username":"root","password":"s3cret"}""", await vault.LoadAsync(id, CancellationToken.None));

        using var conn = _db.Open();
        var blob = await conn.ExecuteScalarAsync<byte[]>("SELECT blob_encrypted FROM credentials WHERE id = @id", new { id });
        var text = System.Text.Encoding.UTF8.GetString(blob!);
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain("root", text);

        // Rotation rewrites the ciphertext.
        await vault.UpdateAsync(id, """{"username":"root","password":"newsecret"}""", CancellationToken.None);
        Assert.Equal("""{"username":"root","password":"newsecret"}""", await vault.LoadAsync(id, CancellationToken.None));

        // Wrong key file → decryption fails loudly (fail closed).
        var otherVault = new CredentialVault(blobStore, Path.Combine(_dir, "other.key"), new SystemClock());
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => otherVault.LoadAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task SessionStore_SlidingExpiry_And_Revocation()
    {
        var store = new SessionStore(_db);
        var now = DateTimeOffset.UtcNow;
        var id = await store.CreateAsync(now, TimeSpan.FromDays(14), CancellationToken.None);

        var session = await store.ValidateAsync(id, now.AddDays(5), CancellationToken.None);
        Assert.NotNull(session);
        // Sliding: expiry extends from the new use.
        Assert.True(session!.ExpiresAt > now.AddDays(14));

        // Expired sessions are rejected and removed.
        var expired = await store.ValidateAsync(id, now.AddDays(20), CancellationToken.None);
        Assert.Null(expired);

        var id2 = await store.CreateAsync(now, TimeSpan.FromDays(14), CancellationToken.None);
        await store.RevokeAsync(id2, CancellationToken.None);
        Assert.Null(await store.ValidateAsync(id2, now.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task Outbox_RetryBackoff_And_FinalFailure()
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO sources(id, kind, name, created_at) VALUES ('src_1','windows-agent','HOST01','2024-01-01T00:00:00.0000000Z')");
        await conn.ExecuteAsync("INSERT INTO notification_channels(id, name, kind, enabled, created, updated_at) VALUES ('ch_1','tg','telegram',1,'2024-01-01T00:00:00.0000000Z','2024-01-01T00:00:00.0000000Z')");

        var store = new OutboxStore(_db);
        var now = DateTimeOffset.Parse("2024-08-07T10:00:00Z");
        await store.EnqueueAsync("al_1", "ch_1", now, CancellationToken.None);

        var due = await store.DequeueDueAsync(10, now, CancellationToken.None);
        var item = Assert.Single(due);
        Assert.Equal("sending", item.Status);

        // Failures retry with growing backoff (1m, then 2m, ...).
        await store.MarkResultAsync(item.Id, false, "telegram 500", now, CancellationToken.None);
        Assert.Empty(await store.DequeueDueAsync(10, now.AddSeconds(30), CancellationToken.None)); // not due yet
        var retry = await store.DequeueDueAsync(10, now.AddMinutes(1), CancellationToken.None);
        Assert.Single(retry);
        await store.MarkResultAsync(item.Id, false, "telegram 500", now.AddMinutes(1), CancellationToken.None);
        Assert.Empty(await store.DequeueDueAsync(10, now.AddMinutes(2), CancellationToken.None)); // backoff grew to 2m
        var retry2 = await store.DequeueDueAsync(10, now.AddMinutes(3), CancellationToken.None);
        Assert.Single(retry2);

        // Success marks sent.
        await store.MarkResultAsync(item.Id, true, null, now.AddMinutes(3), CancellationToken.None);
        Assert.Empty(await store.DequeueDueAsync(10, now.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task BackupStore_VacuumInto_And_Prune()
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO sources(id, kind, name, created_at) VALUES ('src_1','windows-agent','HOST01','2024-01-01T00:00:00.0000000Z')");
        var backupDir = Path.Combine(_dir, "backup");
        var store = new BackupStore(_db, backupDir);

        var result = await store.CreateSnapshotAsync(DateTimeOffset.Parse("2024-08-07T10:00:00Z"), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(File.Exists(result.Path));

        // The snapshot is a valid standalone database containing the data.
        var snap = new SqliteDb(result.Path);
        using (var sc = snap.Open())
        {
            var count = await sc.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sources");
            Assert.Equal(1, count);
        }

        // Older snapshots beyond the ladder are pruned.
        foreach (var days in new[] { 2, 3, 8, 9, 10, 40, 41 })
        {
            await store.CreateSnapshotAsync(DateTimeOffset.Parse("2024-08-07T10:00:00Z").AddDays(-days), CancellationToken.None);
        }
        await store.PruneAsync(DateTimeOffset.Parse("2024-08-07T10:00:00Z"), CancellationToken.None);
        var remaining = await store.ListAsync(CancellationToken.None);
        Assert.NotEmpty(remaining);
        Assert.All(remaining, b => Assert.True(File.Exists(b.Path)));
    }

    [Fact]
    public async Task RegistrationTokens_SingleUse_Enforced()
    {
        var store = new RegistrationTokenStore(_db);
        var (id, raw) = await store.CreateAsync("windows-agent", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.StartsWith("reg_", raw);

        var lookup = await store.LookupAsync(raw, CancellationToken.None);
        Assert.NotNull(lookup);
        Assert.Equal("windows-agent", lookup!.Kind);
        Assert.Null(lookup.ConsumedAt);

        await store.MarkConsumedAsync(id, DateTimeOffset.UtcNow, CancellationToken.None);
        var after = await store.LookupAsync(raw, CancellationToken.None);
        Assert.NotNull(after!.ConsumedAt);

        Assert.True(await store.RevokeAsync(id, CancellationToken.None));
        var revoked = await store.LookupAsync(raw, CancellationToken.None);
        Assert.True(revoked!.Revoked);
    }

    [Fact]
    public async Task RegistrationUnit_IsAtomic_SingleUseAndSourceReuse()
    {
        var tokens = new RegistrationTokenStore(_db);
        var (_, rawReg) = await tokens.CreateAsync("windows-agent", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        var unit = new RegistrationUnit(_db);

        // First use: creates the source, mints the token, consumes the reg_ token.
        var first = await unit.ExecuteAsync(rawReg, "windows-agent", "HOST-ATOMIC", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(RegistrationStatus.Ok, first.Status);
        Assert.True(first.SourceCreated);
        Assert.NotNull(first.SourceId);
        Assert.StartsWith("agt_", first.RawToken);
        Assert.Equal(["ingest"], first.Scopes);

        // Replay of the same reg_ token → consumed (PROTOCOL §5.4), no second token.
        var replay = await unit.ExecuteAsync(rawReg, "windows-agent", "HOST-ATOMIC", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(RegistrationStatus.Consumed, replay.Status);

        // Reinstall path: a fresh reg_ token for the same (kind, hostname)
        // reuses the source and mints a fresh agent token (PROTOCOL §5.2).
        var (_, rawReg2) = await tokens.CreateAsync("windows-agent", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await unit.ExecuteAsync(rawReg2, "windows-agent", "HOST-ATOMIC", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(RegistrationStatus.Ok, second.Status);
        Assert.False(second.SourceCreated);
        Assert.Equal(first.SourceId, second.SourceId);
        Assert.NotEqual(first.RawToken, second.RawToken);

        // Only one token row was ever minted for the consumed reg_ token.
        using var conn = _db.Open();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tokens WHERE source_id = @sid", new { sid = first.SourceId });
        Assert.Equal(2, count);

        // Wrong kind → KindMismatch; unknown token → UnknownToken; revoked → Revoked.
        var (_, rawReg3) = await tokens.CreateAsync("syslog-feed", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(RegistrationStatus.KindMismatch,
            (await unit.ExecuteAsync(rawReg3, "windows-agent", "HOST-ATOMIC", DateTimeOffset.UtcNow, CancellationToken.None)).Status);
        Assert.Equal(RegistrationStatus.UnknownToken,
            (await unit.ExecuteAsync("reg_nonexistent", "windows-agent", "HOST-ATOMIC", DateTimeOffset.UtcNow, CancellationToken.None)).Status);
        var (revId, rawReg4) = await tokens.CreateAsync("windows-agent", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        await tokens.RevokeAsync(revId, CancellationToken.None);
        Assert.Equal(RegistrationStatus.Revoked,
            (await unit.ExecuteAsync(rawReg4, "windows-agent", "HOST-ATOMIC", DateTimeOffset.UtcNow, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task RegistrationUnit_ParallelSameRegToken_OnlyOneWins()
    {
        var tokens = new RegistrationTokenStore(_db);
        var (_, rawReg) = await tokens.CreateAsync("windows-agent", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        var unit = new RegistrationUnit(_db);

        // Four concurrent registrations with the same reg_ token: the BEGIN
        // IMMEDIATE transaction serializes writers, so exactly one succeeds
        // and the rest observe the consumed flag (API.md §6.2 single-use).
        var results = await Task.WhenAll(
            unit.ExecuteAsync(rawReg, "windows-agent", "RACE-HOST", DateTimeOffset.UtcNow, CancellationToken.None),
            unit.ExecuteAsync(rawReg, "windows-agent", "RACE-HOST", DateTimeOffset.UtcNow, CancellationToken.None),
            unit.ExecuteAsync(rawReg, "windows-agent", "RACE-HOST", DateTimeOffset.UtcNow, CancellationToken.None),
            unit.ExecuteAsync(rawReg, "windows-agent", "RACE-HOST", DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(1, results.Count(r => r.Status == RegistrationStatus.Ok));
        Assert.Equal(3, results.Count(r => r.Status == RegistrationStatus.Consumed));
        Assert.Equal(1, results.Where(r => r.SourceId is not null).Select(r => r.SourceId).Distinct().Count());

        using var conn = _db.Open();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sources"));       // UNIQUE(kind, name)
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tokens"));         // exactly one agt_ token minted
    }
}

public class RedfishNormalizationTests
{
    private static readonly string SystemJson = """
        {"Name":"System","Status":{"State":"Enabled","Health":"OK","HealthRollup":"OK"},
         "Processors":{"@odata.id":"/Systems/1/Processors"},"Memory":{"@odata.id":"/Systems/1/Memory"}}
        """;
    private static readonly string ProcessorsJson = """
        {"Members":[
          {"Name":"CPU1","Status":{"State":"Enabled","Health":"OK"}},
          {"Name":"CPU2","Status":{"State":"Enabled","Health":"OK"}}]}
        """;
    private static readonly string MemoryJson = """
        {"Members":[
          {"Name":"DIMM.A1","Status":{"State":"Enabled","Health":"OK"}}]}
        """;
    private static readonly string ThermalJson = """
        {"Temperatures":[{"Name":"System Board Inlet Temp","ReadingCelsius":28.5,"Status":{"State":"Enabled","Health":"OK"}},
                          {"Name":"CPU1 Temp","ReadingCelsius":52.0,"Status":{"State":"Enabled","Health":"Warning"}}],
         "Fans":[{"Name":"Fan1","Status":{"State":"Enabled","Health":"OK"}}]}
        """;
    private static readonly string PowerJson = """
        {"PowerSupplies":[{"Name":"PSU1","Status":{"State":"Enabled","Health":"OK"}},
                          {"Name":"PSU2","Status":{"State":"Enabled","Health":"Critical"}}],
         "PowerControl":[{"PowerConsumedWatts":312.5}]}
        """;
    private static readonly string ChassisJson = """
        {"Oem":{"Dell":{"DellPhysicalDisk":{"Members":[
            {"Name":"Physical Disk 0:1:0","Status":{"State":"Enabled","Health":"Warning"},"FailurePredicted":true}]},
          "DellController":{"Members":[{"Name":"PERC H755","Status":{"State":"Enabled","Health":"OK"}}]}}}}
        """;

    private sealed class FakeHandler(
        string system, string processors, string memory, string thermal, string power, string chassis) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            var json = path.Contains("Thermal") ? thermal
                : path.Contains("Power") ? power
                : path.Contains("Chassis") ? chassis
                : path.Contains("Processors") ? processors
                : path.Contains("Memory") ? memory
                : system;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Poll_Normalizes_VendorNeutralModel()
    {
        var handler = new FakeHandler(SystemJson, ProcessorsJson, MemoryJson, ThermalJson, PowerJson, ChassisJson);
        var factory = new FakeHttpClientFactory(handler);
        var provider = new DellRedfishProvider(factory, IdracCertPolicies.Strict, new NoopCertStore(),
            NullLogger<DellRedfishProvider>.Instance);
        var result = await provider.PollAsync(new HardwarePollTarget("h1", "HOST01",
            "https://idrac.example", "root", "calvin"), CancellationToken.None);
        Assert.Contains("/redfish/v1/Chassis/System.Embedded.1", handler.Requests);

        Assert.True(result.Success);
        // Critical PSU and warning disk drive the rollup to critical.
        Assert.Equal("critical", result.RollupState);

        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Psu && c.Name == "PSU2" && c.State == HealthState.Critical);
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Disk && c.State == HealthState.Warning && c.Detail!.Contains("FailurePredicted=True"));
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Cpu);
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Memory);
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Controller && c.Name == "PERC H755");
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Temp && c.Name == "CPU1 Temp" && c.State == HealthState.Warning);
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Fan);

        Assert.Contains(result.Metrics, m => m.Name == "temperature:CPU1 Temp" && Math.Abs(m.Value - 52.0) < 0.01 && m.Unit == "C");
        Assert.Contains(result.Metrics, m => m.Name == "power:consumed" && Math.Abs(m.Value - 312.5) < 0.01);
    }

    [Fact]
    public async Task Poll_Failure_ReportsError_WithoutComponents()
    {
        var factory = new FakeHttpClientFactory(new FailHandler());
        var provider = new DellRedfishProvider(factory, IdracCertPolicies.Strict, new NoopCertStore(),
            NullLogger<DellRedfishProvider>.Instance);
        var result = await provider.PollAsync(new HardwarePollTarget("h1", "HOST01",
            "https://idrac.example", "root", "calvin"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Components);
    }

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class NoopCertStore : IIdracCertStore
    {
        public Task<IdracCertPin?> GetPinAsync(string hostId, CancellationToken ct) => Task.FromResult<IdracCertPin?>(null);
        public Task<string?> GetFingerprintAsync(string hostId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetAsync(string hostId, byte[] certDer, string fingerprint, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string hostId, CancellationToken ct) => Task.CompletedTask;
    }
}
