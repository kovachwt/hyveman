using Dapper;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Hyveman.Application;
using Hyveman.Contracts;
using Hyveman.Domain;
using Hyveman.Infrastructure.Notify;
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
        // D1: the store reports the exact accepted subset, not a prefix.
        Assert.Equal(["41235", "41236"], first.AcceptedItems.Select(i => i.RecordId).ToArray());

        // Replay: the unique key collapses duplicates.
        var replay = await store.InsertBatchAsync("src_1",
            [Item("41235", "System", "unexpected shutdown again", 3),
             Item("e1:1", "System", "after channel clear", 4)], CancellationToken.None);
        Assert.Equal(1, replay.Accepted);
        Assert.Equal(1, replay.Deduped);
        // D1: mixed batch — item 1 deduped, item 2 accepted; the subset is [item 2].
        Assert.Equal(["e1:1"], replay.AcceptedItems.Select(i => i.RecordId).ToArray());
        Assert.Equal("after channel clear", Assert.Single(replay.AcceptedItems).Message);

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

    // ── alerts (DEFECTS.md D2/D3) ──────────────────────────────────────────

    private async Task SeedAlertParentsAsync()
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync("INSERT INTO sources(id, kind, name, created_at) VALUES ('src_1','windows-agent','HOST01','2024-01-01T00:00:00.0000000Z')");
        await conn.ExecuteAsync("INSERT INTO hosts(id, name, kind, enabled, updated_at, created_at) " +
            "VALUES ('hst_1','HOST01','windows-server',1,'2024-01-01T00:00:00.0000000Z','2024-01-01T00:00:00.0000000Z')");
        await conn.ExecuteAsync("INSERT INTO rules(id, name, type, match_json, severity, cooldown_s, enabled, created_at, updated_at) " +
            "VALUES ('rul_1','6008','event','{}','warning',0,1,'2024-01-01T00:00:00.0000000Z','2024-01-01T00:00:00.0000000Z')");
    }

    private static AlertRecord Alert(string id, string key, string status, DateTimeOffset at) => new(
        id, "rul_1", "hst_1", "src_1", key, "event:System:6008", "warning", status,
        "Event 6008", null, at, at, 1, null, null, null, null, at);

    [Fact]
    public async Task Alerts_TwoFireResolveCycles_SameKey_KeepHistory()
    {
        // D2: UNIQUE(key, status) made the second resolve of an alert key throw
        // (SQLite Error 19). Migration V5 replaced it with a partial unique
        // index over live statuses; two full cycles on one key must both
        // resolve and both rows must remain in history.
        await SeedAlertParentsAsync();
        var store = new AlertStore(_db);
        var at = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var key = "rul_1|hst_1|src_1|event:System:6008";

        // Cycle 1: fire → resolve.
        await store.CreateAsync(Alert("al_1", key, AlertStatuses.Active, at), CancellationToken.None);
        await store.UpdateAsync(Alert("al_1", key, AlertStatuses.Resolved, at) with
        {
            ResolvedAt = at.AddMinutes(1), UpdatedAt = at.AddMinutes(1),
        }, CancellationToken.None);

        // Cycle 2: fire → resolve. The resolve previously collided with al_1.
        var at2 = at.AddDays(1);
        await store.CreateAsync(Alert("al_2", key, AlertStatuses.Active, at2), CancellationToken.None);
        await store.UpdateAsync(Alert("al_2", key, AlertStatuses.Resolved, at2) with
        {
            ResolvedAt = at2.AddMinutes(1), UpdatedAt = at2.AddMinutes(1),
        }, CancellationToken.None);

        var all = await store.ListAsync(new AlertQuery(null, null, null, null, null, 50, null), CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a.Id == "al_1" && a.Status == AlertStatuses.Resolved);
        Assert.Contains(all, a => a.Id == "al_2" && a.Status == AlertStatuses.Resolved);
        Assert.All(all, a => Assert.Equal(key, a.Key));

        // No live occurrence remains for the key.
        Assert.Null(await store.FindLiveAsync(key, CancellationToken.None));
        // GetLatestAsync (D3 cooldown lookup) returns the most recent occurrence.
        var latest = await store.GetLatestAsync(key, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal("al_2", latest!.Id);
    }

    [Fact]
    public async Task Alerts_PartialIndex_EnforcesOneLivePerKey()
    {
        // The migration drops UNIQUE(key, status) and installs a partial unique
        // index over live statuses: still at most one live occurrence per key
        // (now across active/acknowledged/silenced), while resolved history is
        // unconstrained.
        using var conn = _db.Open();
        var idx = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'ux_alerts_live_key'");
        Assert.NotNull(idx);

        await SeedAlertParentsAsync();
        var store = new AlertStore(_db);
        var at = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var key = "rul_1|hst_1|src_1|event:System:6008";
        await store.CreateAsync(Alert("al_1", key, AlertStatuses.Active, at), CancellationToken.None);

        // A second live occurrence (any live status) is refused.
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            store.CreateAsync(Alert("al_2", key, AlertStatuses.Acknowledged, at), CancellationToken.None));

        // Resolved history alongside a live occurrence is fine.
        await store.CreateAsync(Alert("al_3", key, AlertStatuses.Resolved, at), CancellationToken.None);
        var all = await store.ListAsync(new AlertQuery(null, null, null, null, null, 50, null), CancellationToken.None);
        Assert.Equal(2, all.Count);
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
    public async Task ChannelCreate_StoresNormalizedConfig_NotRawDtoShape()
    {
        // D-regression: channel creation used to serialize the raw
        // ChannelSecretInput DTO (telegramBotToken/... keys) into the vault,
        // while notifiers read the normalized MergeConfig keys (botToken/...).
        // Test notifications then failed with KeyNotFoundException
        // ("The given key was not present in the dictionary.").
        var keyPath = Path.Combine(_dir, "vault.key");
        var vault = new CredentialVault(new CredentialBlobStore(_db), keyPath, new SystemClock());
        var channels = new ChannelStore(_db);
        var service = new ChannelsService(channels, vault, new NoopSender(), new SystemClock(), new AuditStore(_db));

        var dto = await service.CreateAsync(new ChannelInput
        {
            Name = "Ops Telegram",
            Kind = ChannelKinds.Telegram,
            Enabled = true,
            Config = new ChannelSecretInput { TelegramBotToken = "123:abc", TelegramChatId = "-100123" },
        }, "admin", CancellationToken.None);

        var stored = await vault.LoadAsync((await channels.GetAsync(dto.Id, CancellationToken.None))!.ConfigRef!, CancellationToken.None);
        Assert.NotNull(stored);
        using var doc = JsonDocument.Parse(stored!);
        Assert.Equal("123:abc", doc.RootElement.GetProperty("botToken").GetString());
        Assert.Equal("-100123", doc.RootElement.GetProperty("chatId").GetString());
        Assert.False(doc.RootElement.TryGetProperty("telegramBotToken", out _));
        Assert.False(doc.RootElement.TryGetProperty("telegramChatId", out _));
    }

    [Fact]
    public async Task TelegramNotifier_AcceptsBothConfigKeySpellings()
    {
        // Configs written before the normalization fix carry the raw DTO keys;
        // the notifier must accept both spellings instead of throwing
        // KeyNotFoundException.
        var handler = new OkHandler();
        var notifier = new TelegramNotifier(new FakeHttpClientFactory(handler), NullLogger<TelegramNotifier>.Instance);
        var msg = new NotificationMessage("Hyveman test notification", "body", "info", "tg");

        var legacy = await notifier.SendAsync(msg,
            """{"telegramBotToken":"123:abc","telegramChatId":"-100123"}""", CancellationToken.None);
        Assert.True(legacy.Ok);

        var normalized = await notifier.SendAsync(msg,
            """{"botToken":"123:abc","chatId":"-100123"}""", CancellationToken.None);
        Assert.True(normalized.Ok);

        Assert.All(handler.Requests, r => Assert.EndsWith("/bot123:abc/sendMessage", r));
    }

    [Fact]
    public async Task TelegramNotifier_MissingKeys_ReturnsCleanError()
    {
        var notifier = new TelegramNotifier(new FakeHttpClientFactory(new FailHandler()), NullLogger<TelegramNotifier>.Instance);
        var result = await notifier.SendAsync(new NotificationMessage("t", "b", "info", "tg"),
            "{}", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("telegram config missing botToken/chatId", result.Error);
    }

    [Fact]
    public async Task SessionStore_SlidingExpiry_And_Revocation()
    {
        var store = new SessionStore(_db);
        var now = DateTimeOffset.UtcNow;
        var lifetime = TimeSpan.FromDays(14);
        var id = await store.CreateAsync(now, lifetime, CancellationToken.None);

        // Sliding: a valid use extends expiry by the fixed lifetime from now.
        var session = await store.ValidateAsync(id, now.AddDays(5), lifetime, CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal(now.AddDays(5).Add(lifetime), session!.ExpiresAt);

        // D6 regression: repeated validations across simulated days never
        // compound the window — every slide resets expiry to now + lifetime.
        var cursor = now.AddDays(5);
        for (var i = 0; i < 30; i++)
        {
            cursor = cursor.AddDays(1);
            session = await store.ValidateAsync(id, cursor, lifetime, CancellationToken.None);
            Assert.NotNull(session);
            Assert.Equal(cursor.Add(lifetime), session!.ExpiresAt);
        }
        Assert.True(session!.ExpiresAt <= cursor.Add(lifetime));

        // Expired sessions are rejected and removed.
        var expired = await store.ValidateAsync(id, cursor.Add(lifetime).AddMinutes(1), lifetime, CancellationToken.None);
        Assert.Null(expired);

        var id2 = await store.CreateAsync(now, lifetime, CancellationToken.None);
        await store.RevokeAsync(id2, CancellationToken.None);
        Assert.Null(await store.ValidateAsync(id2, now.AddDays(1), lifetime, CancellationToken.None));
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
    // Fixtures recorded from the fleet iDRAC (HOST-A, 10.x.x.x, iDRAC9,
    // PowerEdge R7415) on 2026-08-09, trimmed to the fields the provider
    // reads. The collection payloads are faithful: members are bare link
    // objects ({"@odata.id": ...}) — the exact shape that defeated the
    // pre-D4 inline-only parsing.
    private static readonly string SystemJson = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1","Name":"System",
         "Status":{"Health":"OK","HealthRollup":"OK","State":"Enabled"},
         "Model":"PowerEdge R7415","Manufacturer":"Dell Inc.","SerialNumber":"EXAMPLE1"}
        """;
    private static readonly string ProcessorsJson = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Processors",
         "Members":[{"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Processors/CPU.Socket.1"}],
         "Members@odata.count":1,"Name":"ProcessorsCollection"}
        """;
    private static readonly string CpuJson = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Processors/CPU.Socket.1",
         "Name":"CPU 1","Status":{"Health":"OK","State":"Enabled"},
         "Model":"AMD EPYC 7551P 32-Core Processor","Manufacturer":"AMD",
         "Socket":"CPU.Socket.1","TotalCores":32,"TotalThreads":64,"MaxSpeedMHz":3000}
        """;
    private static readonly string MemoryJson = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Memory",
         "Members":[
           {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Memory/DIMM.Socket.A1"},
           {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Memory/DIMM.Socket.A2"}],
         "Members@odata.count":8,"Name":"Memory Devices Collection"}
        """;
    private static readonly string DimmA1Json = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Memory/DIMM.Socket.A1",
         "Name":"DIMM A1","Status":{"Health":"OK","State":"Enabled"},
         "Manufacturer":"Samsung","SerialNumber":"DIMMSN01","PartNumber":"M393A2K40BB2-CTD",
         "CapacityMiB":16384,"DeviceLocator":"DIMM A1","MemoryDeviceType":"DDR4","OperatingSpeedMhz":2666}
        """;
    private static readonly string DimmA2Json = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Memory/DIMM.Socket.A2",
         "Name":"DIMM A2","Status":{"Health":"OK","State":"Enabled"},
         "Manufacturer":"Samsung","SerialNumber":"DIMMSN02","PartNumber":"M393A2K40BB2-CTD",
         "CapacityMiB":16384,"DeviceLocator":"DIMM A2","MemoryDeviceType":"DDR4","OperatingSpeedMhz":2666}
        """;
    private static readonly string StorageJson = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage",
         "Members":[
           {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1"},
           {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/AHCI.Slot.5-1"},
           {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/AHCI.Embedded.3-1"},
           {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/PCIeSSD.Slot.2-C"},
           {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/PCIeSSD.Slot.3-C"}],
         "Members@odata.count":5,"Name":"Storage Collection"}
        """;
    private static readonly string RaidControllerJson = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1",
         "Name":"PERC H330 Mini","Status":{"Health":"OK","HealthRollup":"OK","State":"Enabled"},
         "Description":"PERC H330 Mini",
         "Drives":[{"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1/Drives/Disk.Bay.0:Enclosure.Internal.0-1:RAID.Integrated.1-1"}],
         "Drives@odata.count":1}
        """;
    private static readonly string AhciControllerJson = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/AHCI.Embedded.3-1",
         "Name":"FCH SATA Controller [AHCI mode]",
         "Status":{"Health":null,"HealthRollup":null,"State":"Enabled"},
         "Description":"FCH SATA Controller [AHCI mode]","Drives":[],"Drives@odata.count":0}
        """;
    private static readonly string DriveJson = """
        {"@odata.id":"/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1/Drives/Disk.Bay.0:Enclosure.Internal.0-1:RAID.Integrated.1-1",
         "Name":"Physical Disk 0:1:0","Status":{"Health":"OK","HealthRollup":"OK","State":"Enabled"},
         "Model":"TOSHIBA MG04ACA1","Manufacturer":"TOSHIBA","SerialNumber":"DRIVESN0001",
         "PartNumber":"EXAMPLEPARTNUMBER0000001","CapacityBytes":1000204886016,
         "FailurePredicted":false,"MediaType":"HDD","Protocol":"SATA","RotationSpeedRPM":8220,"Revision":"FK5D"}
        """;
    private static readonly string ThermalJson = """
        {"Name":"Thermal",
         "Temperatures":[
           {"Name":"CPU1 Temp","ReadingCelsius":57,"Status":{"Health":"OK","State":"Enabled"}},
           {"Name":"System Board Inlet Temp","ReadingCelsius":25,"Status":{"Health":"OK","State":"Enabled"}},
           {"Name":"System Board Exhaust Temp","ReadingCelsius":30,"Status":{"Health":"OK","State":"Enabled"}}],
         "Fans":[{"Name":"System Board Fan1","Status":{"Health":"OK","State":"Enabled"}}]}
        """;
    private static readonly string PowerJson = """
        {"Name":"Power",
         "PowerSupplies":[
           {"Name":"PS1 Status","Status":{"Health":"OK","State":"Enabled"}},
           {"Name":"PS2 Status","Status":{"Health":"OK","State":"Enabled"}}],
         "PowerControl":[{"Name":"System Power Control","PowerConsumedWatts":162}]}
        """;
    private static readonly string PowerCriticalJson = """
        {"Name":"Power",
         "PowerSupplies":[
           {"Name":"PS1 Status","Status":{"Health":"OK","State":"Enabled"}},
           {"Name":"PS2 Status","Status":{"Health":"Critical","State":"Enabled"}}],
         "PowerControl":[{"Name":"System Power Control","PowerConsumedWatts":162}]}
        """;

    private sealed class FakeHandler(Dictionary<string, string> routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (routes.TryGetValue(path, out var json))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static FakeHandler RealFleetHandler(bool predictiveFailure = false, bool criticalPsu = false)
    {
        var drive = predictiveFailure
            ? DriveJson.Replace("\"FailurePredicted\":false", "\"FailurePredicted\":true")
            : DriveJson;
        return new FakeHandler(new Dictionary<string, string>
        {
            ["/redfish/v1/Systems/System.Embedded.1"] = SystemJson,
            ["/redfish/v1/Systems/System.Embedded.1/Processors"] = ProcessorsJson,
            ["/redfish/v1/Systems/System.Embedded.1/Processors/CPU.Socket.1"] = CpuJson,
            ["/redfish/v1/Systems/System.Embedded.1/Memory"] = MemoryJson,
            ["/redfish/v1/Systems/System.Embedded.1/Memory/DIMM.Socket.A1"] = DimmA1Json,
            ["/redfish/v1/Systems/System.Embedded.1/Memory/DIMM.Socket.A2"] = DimmA2Json,
            ["/redfish/v1/Systems/System.Embedded.1/Storage"] = StorageJson,
            ["/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1"] = RaidControllerJson,
            ["/redfish/v1/Systems/System.Embedded.1/Storage/AHCI.Embedded.3-1"] = AhciControllerJson,
            ["/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1/Drives/Disk.Bay.0:Enclosure.Internal.0-1:RAID.Integrated.1-1"] = drive,
            ["/redfish/v1/Chassis/System.Embedded.1/Thermal"] = ThermalJson,
            ["/redfish/v1/Chassis/System.Embedded.1/Power"] = criticalPsu ? PowerCriticalJson : PowerJson,
        });
    }

    [Fact]
    public async Task Poll_RealFleetPayloads_NormalizesAllComponentTypes()
    {
        var handler = RealFleetHandler();
        var factory = new FakeHttpClientFactory(handler);
        var provider = new DellRedfishProvider(factory, IdracCertPolicies.Strict, new NoopCertStore(),
            NullLogger<DellRedfishProvider>.Instance);
        var result = await provider.PollAsync(new HardwarePollTarget("h1", "HOST01",
            "https://idrac.example", "root", "calvin"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ok", result.RollupState);

        // Link-only collection members must be followed (D4): a CPU, DIMMs,
        // storage controllers and a physical disk all arrive as components.
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Cpu && c.Name == "CPU 1"
            && c.State == HealthState.Ok && c.Detail!.Contains("Model=AMD EPYC 7551P 32-Core Processor"));
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Memory && c.Name == "DIMM A1"
            && c.Detail!.Contains("Manufacturer=Samsung"));
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Memory && c.Name == "DIMM A2");
        Assert.Equal(2, result.Components.Count(c => c.Type == ComponentTypes.Memory));
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Controller && c.Name == "PERC H330 Mini"
            && c.State == HealthState.Ok);
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Controller && c.Name == "FCH SATA Controller [AHCI mode]");
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Disk && c.Name == "Physical Disk 0:1:0"
            && c.State == HealthState.Ok && c.Detail!.Contains("CapacityBytes=1000204886016")
            && c.Detail.Contains("FailurePredicted=False"));

        // Thermal/power paths unchanged: inline sensors.
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Temp && c.Name == "CPU1 Temp" && c.State == HealthState.Ok);
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Fan && c.Name == "System Board Fan1");
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Psu && c.Name == "PS1 Status");
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Psu && c.Name == "PS2 Status");

        Assert.Contains(result.Metrics, m => m.Name == "temperature:CPU1 Temp" && Math.Abs(m.Value - 57.0) < 0.01 && m.Unit == "C");
        Assert.Contains(result.Metrics, m => m.Name == "power:consumed" && Math.Abs(m.Value - 162.0) < 0.01);

        // Member resources were fetched by following @odata.id links...
        Assert.Contains("/redfish/v1/Systems/System.Embedded.1/Processors/CPU.Socket.1", handler.Requests);
        Assert.Contains("/redfish/v1/Systems/System.Embedded.1/Memory/DIMM.Socket.A1", handler.Requests);
        Assert.Contains("/redfish/v1/Systems/System.Embedded.1/Storage", handler.Requests);
        Assert.Contains("/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1", handler.Requests);
        Assert.Contains("/redfish/v1/Systems/System.Embedded.1/Storage/RAID.Integrated.1-1/Drives/Disk.Bay.0:Enclosure.Internal.0-1:RAID.Integrated.1-1", handler.Requests);
        // ...and the dead chassis OEM path is gone.
        Assert.DoesNotContain("/redfish/v1/Chassis/System.Embedded.1", handler.Requests);
    }

    [Fact]
    public async Task Poll_PredictiveFailure_EscalatesDiskToWarning()
    {
        // iDRAC signals an imminent disk failure via FailurePredicted; some
        // firmware keeps Status=OK, so the provider must not trust Status
        // alone (DESIGN §4.4 motivating case).
        var handler = RealFleetHandler(predictiveFailure: true);
        var factory = new FakeHttpClientFactory(handler);
        var provider = new DellRedfishProvider(factory, IdracCertPolicies.Strict, new NoopCertStore(),
            NullLogger<DellRedfishProvider>.Instance);
        var result = await provider.PollAsync(new HardwarePollTarget("h1", "HOST01",
            "https://idrac.example", "root", "calvin"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("warning", result.RollupState);
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Disk && c.State == HealthState.Warning
            && c.Detail!.Contains("FailurePredicted=True"));
    }

    [Fact]
    public async Task Poll_CriticalComponent_DrivesRollupToCritical()
    {
        var handler = RealFleetHandler(criticalPsu: true);
        var factory = new FakeHttpClientFactory(handler);
        var provider = new DellRedfishProvider(factory, IdracCertPolicies.Strict, new NoopCertStore(),
            NullLogger<DellRedfishProvider>.Instance);
        var result = await provider.PollAsync(new HardwarePollTarget("h1", "HOST01",
            "https://idrac.example", "root", "calvin"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("critical", result.RollupState);
        Assert.Contains(result.Components, c => c.Type == ComponentTypes.Psu && c.Name == "PS2 Status"
            && c.State == HealthState.Critical);
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

    private sealed class NoopCertStore : IIdracCertStore
    {
        public Task<IdracCertPin?> GetPinAsync(string hostId, CancellationToken ct) => Task.FromResult<IdracCertPin?>(null);
        public Task<string?> GetFingerprintAsync(string hostId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetAsync(string hostId, byte[] certDer, string fingerprint, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string hostId, CancellationToken ct) => Task.CompletedTask;
    }
}

internal sealed class OkHandler : HttpMessageHandler
{
    public readonly List<string> Requests = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.ToString());
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true,"result":{"message_id":1}}""",
                System.Text.Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class FailHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
}

internal sealed class NoopSender : INotificationSender
{
    public Task<NotificationResult> SendToChannelAsync(string channelId, NotificationMessage message, CancellationToken ct)
        => Task.FromResult(new NotificationResult(true, null, "noop"));
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}
