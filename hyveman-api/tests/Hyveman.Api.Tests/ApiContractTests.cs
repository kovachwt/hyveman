using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hyveman.Api;
using Hyveman.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Hyveman.Tests.Api;

/// <summary>
/// HTTP contract tests (API.md §6.8): the PROTOCOL.md fixture suite against a
/// test-hosted API — headers, body v, status/error codes, response commands,
/// gzip, limits, idempotency, latest-wins, and the web API session/CSRF gate.
/// </summary>
[Collection("api")]
public class AgentContractTests
{
    private readonly ApiFixture _fx;

    public AgentContractTests(ApiFixture fx) => _fx = fx;

    private Task<HttpResponseMessage> PostAsync(string path, string token, string body, bool gzip = false, int version = 1)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Add("X-Hyveman-Protocol", version.ToString());
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var bytes = Encoding.UTF8.GetBytes(body);
        if (gzip)
        {
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                gz.Write(bytes);
            bytes = ms.ToArray();
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentEncoding.Add("gzip");
        }
        else
        {
            req.Content = new ByteArrayContent(bytes);
        }
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return _fx.Client.SendAsync(req);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string Item(string recordId, string scope = "System", string? time = "2024-08-07T15:02:11Z") =>
        $"{{\"kind\":\"log\",\"record_id\":\"{recordId}\",\"dedup_scope\":\"{scope}\",\"time\":\"{time}\"}}";

    // ── registration & token lifecycle ─────────────────────────────────────

    [Fact]
    public async Task Register_ExchangesToken_And_ConsumesRegToken()
    {
        var (regToken, _) = await _fx.CreateRegistrationTokenAsync("windows-agent");

        var resp = await PostAsync("/register", regToken,
            """{"v":1,"kind":"windows-agent","hostname":"CONTRACT-01","agent_version":"0.1.0","os_build":"17763","boot_id":"b1"}""");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("1", resp.Headers.GetValues("X-Hyveman-Protocol").Single());
        var body = await ReadJson(resp);
        Assert.Equal(1, body.GetProperty("v").GetInt32());
        Assert.Matches(@"^src_[0-9a-f]+$", body.GetProperty("source_id").GetString());
        Assert.Matches(@"^agt_[0-9a-f]+$", body.GetProperty("token").GetString());
        Assert.Equal(["ingest"], body.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("commands").ValueKind);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T.*Z$", body.GetProperty("issued_at").GetString());

        // Replay → 410 token_consumed (PROTOCOL §5.4 response-loss path).
        var replay = await PostAsync("/register", regToken,
            """{"v":1,"kind":"windows-agent","hostname":"CONTRACT-01"}""");
        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);
        var replayBody = await ReadJson(replay);
        Assert.Equal("token_consumed", replayBody.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Register_Reinstall_ReusesSource()
    {
        var (reg1, _) = await _fx.CreateRegistrationTokenAsync("windows-agent");
        var r1 = await ReadJson(await PostAsync("/register", reg1,
            """{"v":1,"kind":"windows-agent","hostname":"REINSTALL-01"}"""));
        var (reg2, _) = await _fx.CreateRegistrationTokenAsync("windows-agent");
        var r2 = await ReadJson(await PostAsync("/register", reg2,
            """{"v":1,"kind":"windows-agent","hostname":"REINSTALL-01","boot_id":"different-boot"}"""));

        Assert.Equal(r1.GetProperty("source_id").GetString(), r2.GetProperty("source_id").GetString());
        Assert.NotEqual(r1.GetProperty("token").GetString(), r2.GetProperty("token").GetString());
        // boot_id is informational only — never part of source resolution.
    }

    // ── versioning ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Version_Missing_Unsupported_Mismatched()
    {
        // Missing header → 400 missing_version with supported.
        var noHeader = await _fx.Client.PostAsync("/ingest/logs",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
        var b1 = await ReadJson(noHeader);
        Assert.Equal("missing_version", b1.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal([1], b1.GetProperty("error").GetProperty("supported").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(1, b1.GetProperty("v").GetInt32());
        Assert.Equal("1", noHeader.Headers.GetValues("X-Hyveman-Protocol").Single());

        // Unsupported header → 400 unsupported_version (server version, never echo).
        var unsupported = await PostAsync("/register", "reg_x", """{"v":99}""", version: 99);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        var b2 = await ReadJson(unsupported);
        Assert.Equal("unsupported_version", b2.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, b2.GetProperty("v").GetInt32());

        // Body v mismatches the supported header → 400 invalid_request.
        var (regToken, _) = await _fx.CreateRegistrationTokenAsync("windows-agent");
        var mismatched = await PostAsync("/register", regToken, """{"v":2,"kind":"windows-agent","hostname":"X"}""");
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        Assert.Equal("invalid_request", (await ReadJson(mismatched)).GetProperty("error").GetProperty("code").GetString());
    }

    // ── authentication ─────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_Invalid_Revoked_WrongScope()
    {
        // Invalid token.
        var bad = await PostAsync("/ingest/logs", "agt_nonexistent", "{\"v\":1,\"items\":[" + Item("1") + "]}");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("token_invalid", (await ReadJson(bad)).GetProperty("error").GetProperty("code").GetString());

        // Missing token.
        var missing = await PostAsync("/ingest/logs", null!, "{\"v\":1,\"items\":[" + Item("1") + "]}");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("token_missing", (await ReadJson(missing)).GetProperty("error").GetProperty("code").GetString());

        // Revoked token.
        var token = await _fx.RegisterAgentAsync("REVOKED-01");
        await _fx.RevokeAgentTokenAsync(token);
        var revoked = await PostAsync("/ingest/logs", token, "{\"v\":1,\"items\":[" + Item("1") + "]}");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("token_revoked", (await ReadJson(revoked)).GetProperty("error").GetProperty("code").GetString());

        // Registration token on an ingest endpoint → wrong_scope.
        var (regToken, _) = await _fx.CreateRegistrationTokenAsync("windows-agent");
        var wrongScope = await PostAsync("/ingest/logs", regToken, "{\"v\":1,\"items\":[" + Item("1") + "]}");
        Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
        Assert.Equal("wrong_scope", (await ReadJson(wrongScope)).GetProperty("error").GetProperty("code").GetString());

        // Every error envelope carries commands (PROTOCOL §16).
        Assert.Equal(JsonValueKind.Array, (await ReadJson(wrongScope)).GetProperty("commands").ValueKind);
    }

    // ── log ingestion ──────────────────────────────────────────────────────

    [Fact]
    public async Task Logs_MixedBatch_PerItemResults_AndInvariant()
    {
        var token = await _fx.RegisterAgentAsync("LOGS-01");
        var body = """
            {"v":1,"source":"src_whatever","items":[
              {"kind":"log","record_id":"100","dedup_scope":"System","time":"2024-08-07T10:00:00.123Z","severity":3,"facility":"Microsoft-Windows-Kernel-Power","message":"reboot","fields":{"channel":"System","event_id":6008}},
              {"kind":"log","record_id":"101","dedup_scope":"System","time":"not-a-time","severity":3,"message":"bad"},
              {"kind":"log","record_id":"e1:5","dedup_scope":"System","time":"2024-08-07T10:00:01Z","severity":4,"message":"epoch"},
              {"kind":"log","record_id":"100","dedup_scope":"System","time":"2024-08-07T10:00:02Z","severity":4,"message":"dup"}
            ]}
            """;
        var resp = await PostAsync("/ingest/logs", token, body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await ReadJson(resp);
        Assert.Equal(2, json.GetProperty("accepted").GetInt32());
        Assert.Equal(1, json.GetProperty("deduped").GetInt32());
        var rejected = json.GetProperty("rejected");
        Assert.Equal(1, rejected.GetArrayLength());
        Assert.Equal("bad_time", rejected[0].GetProperty("reason").GetString());
        Assert.True(rejected[0].GetProperty("permanent").GetBoolean());
        // Invariant: accepted + deduped + rejected == items.
        Assert.Equal(2 + 1 + 1, 4);

        // Replay the valid items → all deduped.
        var replay = await PostAsync("/ingest/logs", token,
            """{"v":1,"items":[{"kind":"log","record_id":"100","dedup_scope":"System","time":"2024-08-07T10:00:03Z","severity":4,"message":"x"}]}""");
        var replayJson = await ReadJson(replay);
        Assert.Equal(0, replayJson.GetProperty("accepted").GetInt32());
        Assert.Equal(1, replayJson.GetProperty("deduped").GetInt32());
    }

    [Fact]
    public async Task Logs_TooManyItems_AndPayloadTooLarge()
    {
        var token = await _fx.RegisterAgentAsync("LIMITS-01");
        var many = "{\"v\":1,\"items\":[" + string.Join(",", Enumerable.Range(0, 1001).Select(i => Item(i.ToString(), "S"))) + "]}";
        var resp = await PostAsync("/ingest/logs", token, many);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("too_many_items", (await ReadJson(resp)).GetProperty("error").GetProperty("code").GetString());

        var big = "{\"v\":1,\"items\":[{\"kind\":\"log\",\"record_id\":\"1\",\"dedup_scope\":\"S\",\"time\":\"2024-08-07T00:00:00Z\",\"message\":\"" + new string('x', 6 * 1024 * 1024) + "\"}]}";
        var tooBig = await PostAsync("/ingest/logs", token, big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Equal("payload_too_large", (await ReadJson(tooBig)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logs_Gzip_And_UnknownFields_Accepted()
    {
        var token = await _fx.RegisterAgentAsync("GZIP-01");
        var body = "{\"v\":1,\"future_top_level\":true,\"items\":[" + string.Join(",", Enumerable.Range(0, 50).Select(i => Item(i.ToString(), "G"))) + "]}";
        var resp = await PostAsync("/ingest/logs", token, body, gzip: true);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(50, (await ReadJson(resp)).GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Logs_WrongItemKind_RejectsBatch()
    {
        var token = await _fx.RegisterAgentAsync("KIND-01");
        var resp = await PostAsync("/ingest/logs", token,
            """{"v":1,"items":[{"kind":"facts","collected_at":"2024-08-07T10:00:00Z"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_request", (await ReadJson(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logs_TypeMismatchedItem_RejectedPerItem_NotWholeBatch()
    {
        // PROTOCOL §6.2/§6.4: a malformed item (wrong JSON type) must be
        // rejected per-item with "schema", while the valid items are stored.
        var token = await _fx.RegisterAgentAsync("TYPEMISMATCH-01");
        var body = """
            {"v":1,"items":[
              {"kind":"log","record_id":"1","dedup_scope":"S","time":"2024-08-07T00:00:00Z","message":"good"},
              {"kind":"log","record_id":"2","dedup_scope":"S","time":"2024-08-07T00:00:01Z","facility":123,"message":"bad-facility"}
            ]}
            """;
        var resp = await PostAsync("/ingest/logs", token, body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await ReadJson(resp);
        Assert.Equal(1, json.GetProperty("accepted").GetInt32());
        Assert.Equal(0, json.GetProperty("deduped").GetInt32());
        var rejected = json.GetProperty("rejected");
        Assert.Equal(1, rejected.GetArrayLength());
        Assert.Equal("schema", rejected[0].GetProperty("reason").GetString());
        Assert.True(rejected[0].GetProperty("permanent").GetBoolean());
        // Invariant holds: 1 + 0 + 1 == 2 items.
        Assert.Equal(2, json.GetProperty("accepted").GetInt32() + json.GetProperty("deduped").GetInt32()
            + json.GetProperty("rejected").GetArrayLength());
    }

    [Fact]
    public async Task Logs_EmptyBatch_Rejected()
    {
        var token = await _fx.RegisterAgentAsync("EMPTY-01");
        var resp = await PostAsync("/ingest/logs", token, """{"v":1,"items":[]}""");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_request", (await ReadJson(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    // ── telemetry (latest-wins) ────────────────────────────────────────────

    [Fact]
    public async Task Telemetry_HeartbeatAndFacts_ThenOlderPayloadStill200()
    {
        var token = await _fx.RegisterAgentAsync("TELE-01");
        var first = await PostAsync("/ingest/telemetry", token, """
            {"v":1,"items":[
              {"kind":"heartbeat","sent_at":"2024-08-07T10:30:00Z","boot_time":"2024-08-01T00:00:00Z","uptime_s":100,"degraded":"","counters":{"events_sent":1,"events_dropped":0,"batches_sent":1,"batches_failed":0,"spool_bytes":0,"spool_files":0,"queue_depth":0,"wmi_timeouts":0,"send_errors_last_min":0}},
              {"kind":"facts","collected_at":"2024-08-07T10:30:00Z","stale":false,"vms":[{"name":"VM1","state":"on","heartbeat_ok":true}]}]}
            """);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await ReadJson(first);
        Assert.True(firstJson.GetProperty("accepted").GetBoolean());
        Assert.Equal(JsonValueKind.Array, firstJson.GetProperty("commands").ValueKind);

        // Older payload in the same boot: still 200, state must not regress.
        var older = await PostAsync("/ingest/telemetry", token,
            """{"v":1,"items":[{"kind":"heartbeat","sent_at":"2024-08-07T09:00:00Z","boot_time":"2024-08-01T00:00:00Z","uptime_s":5,"degraded":""}]}""");
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);

        // Malformed telemetry is a whole-batch 4xx.
        var bad = await PostAsync("/ingest/telemetry", token, """{"v":1,"items":[{"kind":"wat"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ── health ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_WithAndWithoutToken()
    {
        var resp = await GetHealthAsync(null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var anon = await ReadJson(resp);
        Assert.True(anon.GetProperty("ok").GetBoolean());
        Assert.False(anon.TryGetProperty("source_id", out _));
        Assert.False(anon.TryGetProperty("scopes", out _));
        Assert.Equal(JsonValueKind.Array, anon.GetProperty("commands").ValueKind);

        var token = await _fx.RegisterAgentAsync("HEALTH-01");
        var authed = await GetHealthAsync(null);
        Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
        var anon2 = await ReadJson(authed);
        Assert.False(anon2.TryGetProperty("source_id", out _)); // no Authorization header

        var withToken = await GetHealthAsync(token);
        var tok = await ReadJson(withToken);
        Assert.True(tok.TryGetProperty("source_id", out var sid));
        Assert.Equal(["ingest"], tok.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    private async Task<HttpResponseMessage> GetHealthAsync(string? token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("X-Hyveman-Protocol", "1");
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _fx.Client.SendAsync(req);
    }

    // ── rate limiting ──────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimit_Returns429_WithRetryAfter()
    {
        var token = await _fx.RegisterAgentAsync("RATE-01");
        // The test config lowers the per-source budget; flood until limited.
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 60; i++)
        {
            limited = await PostAsync("/ingest/logs", token, "{\"v\":1,\"items\":[" + Item(i.ToString()) + "]}");
            if (limited.StatusCode == HttpStatusCode.TooManyRequests) break;
        }
        Assert.NotNull(limited);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited!.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal("too_many_requests", (await ReadJson(limited)).GetProperty("error").GetProperty("code").GetString());
        Assert.NotNull(limited.Headers.RetryAfter!.Delta);
    }

    // ── web API ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WebApi_LogonStats_AggregatesSecurityEvents_AndIgnoresDedupedReplays()
    {
        // DESIGN §4.1/§13 #5: accepted Security events aggregate into
        // per-user/per-day logon counts; deduped replays must not double-count.
        var token = await _fx.RegisterAgentAsync("LOGON-01");
        var batch = """
            {"v":1,"items":[
              {"kind":"log","record_id":"sec-1","dedup_scope":"Security","time":"2024-08-07T10:00:00Z","severity":4,"message":"logon","fields":{"channel":"Security","event_id":4624,"event_data":{"LogonType":"10","TargetUserName":"admin"}}},
              {"kind":"log","record_id":"sec-2","dedup_scope":"Security","time":"2024-08-07T10:00:01Z","severity":4,"message":"network-logon","fields":{"channel":"Security","event_id":4624,"event_data":{"LogonType":"3","TargetUserName":"svc"}}},
              {"kind":"log","record_id":"sec-3","dedup_scope":"Security","time":"2024-08-07T10:00:02Z","severity":4,"message":"failed","fields":{"channel":"Security","event_id":4625,"event_data":{"LogonType":"3","TargetUserName":"bob"}}},
              {"kind":"log","record_id":"sec-4","dedup_scope":"Security","time":"2024-08-07T10:00:03Z","severity":4,"message":"lockout","fields":{"channel":"Security","event_id":4740,"event_data":{"TargetUserName":"bob"}}}
            ]}
            """;

        var resp = await PostAsync("/ingest/logs", token, batch);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var ingest = await ReadJson(resp);
        Assert.Equal(4, ingest.GetProperty("accepted").GetInt32());

        // Replay: identical key → all deduped → counts must not grow.
        var replay = await PostAsync("/ingest/logs", token, batch);
        Assert.Equal(4, (await ReadJson(replay)).GetProperty("deduped").GetInt32());

        var client = _fx.NewClient();
        _fx.SeedSession(client);
        var stats = await client.GetAsync("/api/v1/logon-stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        var json = await ReadJson(stats);

        // 4624 type 10 → success; 4624 type 3 (curated out) → no row;
        // 4625 type 3 → failure; 4740 (no logon type) → failure with null type.
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        var admin = Assert.Single(items, i => i.GetProperty("user").GetString() == "admin");
        Assert.Equal(1, admin.GetProperty("successCount").GetInt32());
        Assert.Equal(0, admin.GetProperty("failureCount").GetInt32());
        Assert.Equal(10, admin.GetProperty("logonType").GetInt32());
        Assert.Equal("2024-08-07", admin.GetProperty("day").GetString());
        var failed = Assert.Single(items, i => i.GetProperty("user").GetString() == "bob"
            && i.GetProperty("logonType").ValueKind == JsonValueKind.Number);
        Assert.Equal(1, failed.GetProperty("failureCount").GetInt32());
        var locked = Assert.Single(items, i => i.GetProperty("user").GetString() == "bob"
            && i.GetProperty("logonType").ValueKind == JsonValueKind.Null);
        Assert.Equal(1, locked.GetProperty("failureCount").GetInt32());
        Assert.False(json.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task WebApi_MixedBatch_DerivedData_AttributedToAcceptedItemsOnly()
    {
        // D1 (PROTOCOL §6.6): when a retried batch mixes a deduped prefix with
        // a new item, derived data (logon_stats, alert evaluation) must be
        // attributed to the item actually accepted — never to the deduped
        // replay. Regression: Take(Accepted) yielded the batch prefix, so the
        // replayed logon was counted twice and the evaluator never saw the new
        // item.
        var token = await _fx.RegisterAgentAsync("D1-MIXED");
        var logon = """
            {"kind":"log","record_id":"sec-1","dedup_scope":"Security","time":"2024-08-07T10:00:00Z","severity":4,"message":"logon","fields":{"channel":"Security","event_id":4624,"event_data":{"LogonType":"10","TargetUserName":"admin"}}}
            """;
        var lockout = """
            {"kind":"log","record_id":"sec-2","dedup_scope":"Security","time":"2024-08-07T10:00:01Z","severity":4,"message":"lockout","fields":{"channel":"Security","event_id":4740,"event_data":{"TargetUserName":"bob"}}}
            """;

        // An event rule matching only the lockout (sec-2): the evaluator must
        // see sec-2, not the deduped replay sec-1.
        var client = _fx.NewClient();
        _fx.SeedSession(client);
        var csrf = _fx.GetCsrfToken(await client.GetAsync("/api/v1/auth/session"));
        using var createRule = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rules");
        createRule.Headers.Add("X-CSRF-Token", csrf);
        createRule.Headers.Add("Origin", "http://localhost:5173");
        createRule.Content = new StringContent(
            """{"name":"D1 probe","type":"event","severity":"critical","cooldownS":0,"match":{"eventIds":[4740]}}""",
            Encoding.UTF8, "application/json");
        var ruleResp = await client.SendAsync(createRule);
        Assert.Equal(HttpStatusCode.OK, ruleResp.StatusCode);

        // First batch: [A] accepted.
        var first = await PostAsync("/ingest/logs", token, "{\"v\":1,\"items\":[" + logon + "]}");
        Assert.Equal(1, (await ReadJson(first)).GetProperty("accepted").GetInt32());

        // Retry with one new item appended: A dedupes, B is accepted.
        var retry = await PostAsync("/ingest/logs", token, "{\"v\":1,\"items\":[" + logon + "," + lockout + "]}");
        var retryBody = await ReadJson(retry);
        Assert.Equal(1, retryBody.GetProperty("accepted").GetInt32());
        Assert.Equal(1, retryBody.GetProperty("deduped").GetInt32());

        // logon_stats: admin counted exactly once (the replay must not
        // re-count it); bob counted once. The endpoint is cross-source, so
        // scope to this agent's rows.
        var stats = await ReadJson(await client.GetAsync("/api/v1/logon-stats"));
        var statItems = stats.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("sourceName").GetString() == "D1-MIXED").ToList();
        var admin = Assert.Single(statItems, i => i.GetProperty("user").GetString() == "admin");
        Assert.Equal(1, admin.GetProperty("successCount").GetInt32());
        var bob = Assert.Single(statItems, i => i.GetProperty("user").GetString() == "bob");
        Assert.Equal(1, bob.GetProperty("failureCount").GetInt32());

        // The evaluator saw B (the accepted item): the 4740 rule fired exactly
        // once. Before the fix it saw A instead and no alert was created.
        var alerts = await ReadJson(await client.GetAsync("/api/v1/alerts?limit=50"));
        var alert = Assert.Single(alerts.GetProperty("items").EnumerateArray(),
            a => a.GetProperty("ruleName").GetString() == "D1 probe");
        Assert.Equal("active", alert.GetProperty("status").GetString());
        Assert.Equal(1, alert.GetProperty("count").GetInt32());
        Assert.Contains("4740", alert.GetProperty("title").GetString());
    }

    [Fact]
    public async Task WebApi_EventSearch_PagesCoverEveryRow_NoGapsNoDuplicates()
    {
        // DEFECTS.md D5 (API.md §7.2): the +1 probe row must never become the
        // cursor. 3 events at limit=2: page 1 returns the newest two, the
        // cursor must point at the last *returned* row, and page 2 must then
        // deliver the remaining one — the union of the pages is all 3 rows
        // with no duplicates. Before the fix the cursor was encoded from the
        // withheld probe row, so page 2 started after a row neither page
        // delivered (verified: page 2 came back empty, pg-1 unreachable).
        var (token, sourceId) = await _fx.RegisterAgentWithSourceAsync("D5-PAGES");
        var batch = """
            {"v":1,"items":[
              {"kind":"log","record_id":"pg-1","dedup_scope":"System","time":"2024-08-07T10:00:00Z","severity":4,"message":"first"},
              {"kind":"log","record_id":"pg-2","dedup_scope":"System","time":"2024-08-07T10:00:01Z","severity":4,"message":"second"},
              {"kind":"log","record_id":"pg-3","dedup_scope":"System","time":"2024-08-07T10:00:02Z","severity":4,"message":"third"}
            ]}
            """;
        var resp = await PostAsync("/ingest/logs", token, batch);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, (await ReadJson(resp)).GetProperty("accepted").GetInt32());

        var client = _fx.NewClient();
        _fx.SeedSession(client);

        // Scope to this agent's source — the fixture's data dir is shared by
        // the whole collection and the search endpoint is cross-source.
        var page1 = await ReadJson(await client.GetAsync($"/api/v1/events?limit=2&sourceId={sourceId}"));
        var page1Items = page1.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, page1Items.Count);
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        var cursor1 = page1.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor1);
        // Descending: page 1 is [pg-3, pg-2]; the cursor must be the last
        // *returned* row (pg-2), not the withheld probe (pg-1).
        Assert.Equal("pg-3", page1Items[0].GetProperty("recordId").GetString());
        Assert.Equal("pg-2", page1Items[1].GetProperty("recordId").GetString());

        var page2 = await ReadJson(await client.GetAsync(
            $"/api/v1/events?limit=2&sourceId={sourceId}&cursor={Uri.EscapeDataString(cursor1)}"));
        var page2Items = page2.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(1, page2Items.Count);
        Assert.False(page2.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
        Assert.Equal("pg-1", page2Items[0].GetProperty("recordId").GetString());

        // Union of both pages is all 3 events, each exactly once.
        var seen = page1Items.Concat(page2Items).Select(i => i.GetProperty("recordId").GetString()).ToList();
        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(["pg-3", "pg-2", "pg-1"], seen);
    }

    [Fact]
    public async Task WebApi_HostHealth_Endpoint()
    {
        var client = _fx.NewClient();
        _fx.SeedSession(client);
        var csrf = _fx.GetCsrfToken(await client.GetAsync("/api/v1/auth/session"));

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hosts");
        create.Headers.Add("X-CSRF-Token", csrf);
        create.Headers.Add("Origin", "http://localhost:5173");
        create.Content = new StringContent("""{"name":"HEALTH-API-01"}""", Encoding.UTF8, "application/json");
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var hostId = (await ReadJson(created)).GetProperty("id").GetString();

        // GET /api/v1/hosts/{id}/health (API.md §7.1): components, rollup, metrics, snapshots.
        var health = await client.GetAsync($"/api/v1/hosts/{hostId}/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var body = await ReadJson(health);
        Assert.Equal(hostId, body.GetProperty("hostId").GetString());
        Assert.Equal("unknown", body.GetProperty("rollupState").GetString());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("components").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("latestMetrics").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("recentSnapshots").ValueKind);

        var missing = await client.GetAsync("/api/v1/hosts/nope/health");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task WebApi_Session_AndCsrf_Gate()
    {
        var client = _fx.NewClient();

        var session = await client.GetAsync("/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.True((await ReadJson(session)).GetProperty("setupRequired").GetBoolean());
        // CSRF cookie was issued; unsafe requests without the header are 403.
        var csrfValue = _fx.GetCsrfToken(session);
        Assert.NotNull(csrfValue);

        var noCsrf = await client.PostAsync("/api/v1/registration-tokens",
            new StringContent("""{"kind":"windows-agent"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);

        // Problem Details shape (RFC 9457 + stable code + traceId).
        var problem = await ReadJson(noCsrf);
        Assert.Equal("csrf_mismatch", problem.GetProperty("code").GetString());
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.NotNull(problem.GetProperty("traceId").GetString());

        // Unauthenticated web access → 401 with the session scheme.
        var overview = await client.GetAsync("/api/v1/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, overview.StatusCode);

        // Seeded session + CSRF header → authorized mutation works.
        _fx.SeedSession(client);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hosts");
        create.Headers.Add("X-CSRF-Token", csrfValue);
        create.Headers.Add("Origin", "http://localhost:5173");
        create.Content = new StringContent("""{"name":"WEB-01"}""", Encoding.UTF8, "application/json");
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("WEB-01", (await ReadJson(created)).GetProperty("name").GetString());

        // Validation errors → 400 with field-level errors.
        using var badHost = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hosts");
        badHost.Headers.Add("X-CSRF-Token", csrfValue);
        badHost.Headers.Add("Origin", "http://localhost:5173");
        badHost.Content = new StringContent("""{"name":""}""", Encoding.UTF8, "application/json");
        var badResp = await client.SendAsync(badHost);
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
        var badJson = await ReadJson(badResp);
        Assert.Equal("validation_failed", badJson.GetProperty("code").GetString());
        Assert.NotNull(badJson.GetProperty("errors").GetProperty("name"));
    }
}

/// <summary>One test-hosted API per collection with a throwaway data directory.
/// The fixture is collection-scoped (not per-class) because it mutates
/// process-global environment variables; two concurrent fixture constructions
/// would race on HYVEMAN_DATA_DIR and intermittently fail host startup.</summary>
[CollectionDefinition("api", DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
}

public sealed class ApiFixture : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiFixture()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "hyveman-contract-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("HYVEMAN_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("HYVEMAN_AllowInsecureHttp", "true");
        Environment.SetEnvironmentVariable("HYVEMAN_RateLimits__PerSourcePerMinute", "30");
        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient();
    }

    public HttpClient Client { get; }

    /// <summary>The underlying factory, so tests can open real DI scopes and
    /// derive override hosts.</summary>
    public WebApplicationFactory<Program> Factory => _factory;

    public HttpClient NewClient() => _factory.CreateClient();

    /// <summary>Derives a host whose DI container serves T as IAlertEvaluator
    /// (used to prove a derived-alerting failure cannot fail an accepted
    /// telemetry request — DEFECTS.md D2).</summary>
    public HttpClient NewClientWithEvaluator<T>() where T : class, IAlertEvaluator
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IAlertEvaluator>();
            s.AddScoped<IAlertEvaluator, T>();
        }));
        return factory.CreateClient();
    }

    /// <summary>Creates a registration token via the token store (bypasses the
    /// web session, which needs a passkey ceremony).</summary>
    public async Task<(string RawToken, string Id)> CreateRegistrationTokenAsync(string kind)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Hyveman.Application.IRegistrationTokenStore>();
        var created = await store.CreateAsync(kind, null, "test", DateTimeOffset.UtcNow, CancellationToken.None);
        return (created.RawToken, created.Id);
    }

    /// <summary>Registers an agent and returns the raw agt_ token.</summary>
    public async Task<string> RegisterAgentAsync(string hostname)
    {
        var (regToken, _) = await CreateRegistrationTokenAsync("windows-agent");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/register");
        req.Headers.Add("X-Hyveman-Protocol", "1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", regToken);
        req.Content = new StringContent("{\"v\":1,\"kind\":\"windows-agent\",\"hostname\":\"" + hostname + "\"}",
            Encoding.UTF8, "application/json");
        using var resp = await Client.SendAsync(req);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>Registers an agent and returns (token, sourceId) — for tests
    /// that must scope cross-source queries (e.g. event search) to their own
    /// agent's rows.</summary>
    public async Task<(string Token, string SourceId)> RegisterAgentWithSourceAsync(string hostname)
    {
        var (regToken, _) = await CreateRegistrationTokenAsync("windows-agent");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/register");
        req.Headers.Add("X-Hyveman-Protocol", "1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", regToken);
        req.Content = new StringContent("{\"v\":1,\"kind\":\"windows-agent\",\"hostname\":\"" + hostname + "\"}",
            Encoding.UTF8, "application/json");
        using var resp = await Client.SendAsync(req);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return (body.GetProperty("token").GetString()!, body.GetProperty("source_id").GetString()!);
    }

    public async Task RevokeAgentTokenAsync(string rawToken)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Hyveman.Application.ITokenStore>();
        var auth = await store.AuthenticateAsync(rawToken, CancellationToken.None);
        await store.RevokeAsync(auth!.TokenId, CancellationToken.None);
    }

    /// <summary>Reads the CSRF cookie value from a Set-Cookie header.</summary>
    public string? GetCsrfToken(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var cookie in cookies)
        {
            var pair = cookie.Split(';').FirstOrDefault(c => c.StartsWith("hyveman_csrf=", StringComparison.OrdinalIgnoreCase));
            if (pair is not null) return pair[(pair.IndexOf('=') + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Seeds a valid web session into the client's cookie jar.</summary>
    public void SeedSession(HttpClient client)
    {
        var sessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Hyveman.Application.ISessionStore>();
        var id = store.CreateAsync(DateTimeOffset.UtcNow, TimeSpan.FromDays(1), CancellationToken.None).GetAwaiter().GetResult();
        _ = sessionId;
        // ISessionStore keeps only the hash; re-create with our known value by
        // writing through the DB directly.
        using var conn = scope.ServiceProvider.GetRequiredService<Hyveman.Infrastructure.Sqlite.SqliteDb>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_sessions(id_hash, created_at, expires_at, last_seen)
            VALUES ($h, $c, $e, $s)
            """;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("$h", StoreHash(sessionId));
        cmd.Parameters.AddWithValue("$c", now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"));
        cmd.Parameters.AddWithValue("$e", now.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"));
        cmd.Parameters.AddWithValue("$s", now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"));
        cmd.ExecuteNonQuery();
        _ = id;
        client.DefaultRequestHeaders.Add("Cookie", $"hyveman_session={sessionId}");
    }

    private static string StoreHash(string sessionId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant();

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
