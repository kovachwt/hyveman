using System.Text.Json;
using Hyveman.Domain;
using Hyveman.Protocol;
using Xunit;

namespace Hyveman.Tests.Protocol;

public class SchemaValidatorTests
{
    private static readonly SchemaValidator V = ProtocolSchema.Validator;

    [Theory]
    [InlineData("""{"v":1,"kind":"windows-agent","hostname":"HOST01"}""")]
    [InlineData("""{"v":1,"kind":"syslog-feed","hostname":"router","agent_version":"0.1.0","os_build":"x","boot_id":"boot-1","unknown_future_field":42}""")]
    public void RegisterRequests_Valid(string json) => Assert.Empty(V.Validate("#", json));

    [Theory]
    [InlineData("""{"kind":"windows-agent","hostname":"HOST01"}""", "missing v")]
    [InlineData("""{"v":2,"kind":"windows-agent","hostname":"HOST01"}""", "v must be 1")]
    [InlineData("""{"v":1,"hostname":"HOST01"}""", "missing kind")]
    [InlineData("""{"v":1,"kind":"windows-agent"}""", "missing hostname")]
    [InlineData("""{"v":1,"kind":"Windows-Agent","hostname":"H"}""", "kind pattern")]
    [InlineData("""{"v":1,"kind":"windows-agent","hostname":""}""", "empty hostname")]
    public void RegisterRequests_Invalid(string json, string why)
        => Assert.NotEmpty(V.Validate("#", json));

    [Theory]
    [InlineData("""{"v":1,"source_id":"src_01HW","token":"agt_01HW","scopes":["ingest"],"issued_at":"2024-08-07T15:02:11Z","commands":[]}""")]
    [InlineData("""{"v":1,"source_id":"src_x","token":"agt_y","scopes":["ingest"],"issued_at":"2024-08-07T15:02:11.123Z","commands":[]}""")]
    public void RegisterResponses_Valid(string json) => Assert.Empty(V.Validate("#", json));

    [Theory]
    [InlineData("""{"v":1,"source_id":"src_x","token":"bad_token","scopes":["ingest"],"issued_at":"2024-08-07T15:02:11Z","commands":[]}""")]
    [InlineData("""{"v":1,"source_id":"src_x","token":"agt_y","scopes":[],"issued_at":"2024-08-07T15:02:11Z","commands":[]}""")]
    [InlineData("""{"v":1,"source_id":"src_x","token":"agt_y","scopes":["ingest"],"issued_at":"2024-08-07T15:02:11+02:00","commands":[]}""")]
    public void RegisterResponses_Invalid(string json) => Assert.NotEmpty(V.Validate("#", json));

    [Fact]
    public void LogsRequest_Valid_WithUnknownFields()
    {
        var json = """
            {"v":1,"source":"src_01HW","future_envelope_field":true,"items":[
              {"kind":"log","record_id":"41235","dedup_scope":"System",
               "time":"2024-08-07T15:02:11.123Z","severity":3,
               "facility":"Microsoft-Windows-Kernel-Power",
               "message":"The system ...","future_item_field":null,
               "fields":{"channel":"System","event_id":6008,"event_data":{"LogonType":10,"TargetUserName":"admin"},"future_key":"x"},
               "raw":"<Event xmlns='x'/>"}]}
            """;
        Assert.Empty(V.Validate("#", json));
    }

    [Fact]
    public void LogsRequest_UnknownOptionalFields_AreForwardCompatible()
    {
        // PROTOCOL §3 additive rule: unknown members must not fail validation.
        var json = """{"v":1,"items":[{"kind":"log","record_id":"1","dedup_scope":"S","time":"2024-08-07T15:02:11Z","message":"m","extra":123,"fields":{"zz":true}}]}""";
        Assert.Empty(V.Validate("#", json));
    }

    [Fact]
    public void LogsRequest_EmptyItems_Invalid()
    {
        Assert.NotEmpty(V.Validate("#", """{"v":1,"items":[]}"""));
    }

    [Theory]
    [InlineData("""{"v":1,"accepted":12,"deduped":0,"rejected":[],"commands":[]}""")]
    [InlineData("""{"v":1,"accepted":0,"deduped":2,"rejected":[{"record_id":"999","dedup_scope":"System","reason":"bad_time","permanent":true}],"commands":[]}""")]
    public void LogsResponses_Valid(string json) => Assert.Empty(V.Validate("#", json));

    [Fact]
    public void TelemetryRequest_HeartbeatAndFacts()
    {
        var json = """
            {"v":1,"items":[
              {"kind":"heartbeat","sent_at":"2024-08-07T15:02:11Z","agent_version":"0.1.0",
               "protocol_version":1,"os_build":"17763","boot_time":"2024-08-01T00:00:00Z",
               "uptime_s":123456,"degraded":"","config_hash":"a1b2c3",
               "counters":{"events_sent":1,"events_dropped":0,"batches_sent":1,"batches_failed":0,
                           "spool_bytes":0,"spool_files":0,"queue_depth":0,"wmi_timeouts":0,"send_errors_last_min":0}},
              {"kind":"facts","collected_at":"2024-08-07T15:02:10Z","stale":false,
               "vms":[{"name":"VM1","state":"on","heartbeat_ok":true,"cpu_pct":12.3,"mem_mb":4096,
                       "last_seen":"2024-08-07T15:02:09Z"}]}]}
            """;
        Assert.Empty(V.Validate("#", json));
    }

    [Fact]
    public void TelemetryRequest_NullableFactFields_NullValuesValid()
    {
        // PROTOCOL §7.1: heartbeat_ok/cpu_pct/mem_mb are nullable. JSON null
        // members must validate clean and must NOT crash the validator
        // (JsonObject stores them as a null JsonNode — regression for the
        // NullReferenceException on /ingest/telemetry).
        var json = """
            {"v":1,"items":[
              {"kind":"facts","collected_at":"2024-08-07T15:02:10Z","stale":false,
               "vms":[{"name":"VM1","state":"on","heartbeat_ok":null,"cpu_pct":null,"mem_mb":null,
                       "last_seen":"2024-08-07T15:02:09Z"}]}]}
            """;
        Assert.Empty(V.Validate("#", json));
    }

    [Fact]
    public void TelemetryRequest_EmptyVmList_IsValid()
    {
        // PROTOCOL §7.4: "vms": [] with stale:false means the scan succeeded.
        var json = """{"v":1,"items":[{"kind":"facts","collected_at":"2024-08-07T15:02:10Z","stale":false,"vms":[]}]}""";
        Assert.Empty(V.Validate("#", json));
    }

    [Theory]
    [InlineData("""{"v":1,"items":[{"kind":"heartbeat"}]}""", "missing sent_at")]
    [InlineData("""{"v":1,"items":[{"kind":"heartbeat","sent_at":"2024-08-07T15:02:11+02:00"}]}""", "offset not Z")]
    [InlineData("""{"v":1,"items":[{"kind":"heartbeat","sent_at":"2024-08-07T15:02:11Z","degraded":"bogus"}]}""", "bad degraded")]
    [InlineData("""{"v":1,"items":[{"kind":"facts","collected_at":"2024-08-07T15:02:10Z","vms":[{"name":"VM","state":"exploded"}]}]}""", "bad vm state")]
    [InlineData("""{"v":1,"items":[{"kind":"wat"}]}""", "unknown kind")]
    [InlineData("""{"v":1,"items":[{"kind":"heartbeat","sent_at":"2024-08-07T15:02:11Z","boot_time":"not-a-time"}]}""", "bad boot_time")]
    public void TelemetryRequest_Invalid(string json, string why)
        => Assert.NotEmpty(V.Validate("#", json));

    [Fact]
    public void TelemetryResponse_Valid()
        => Assert.Empty(V.Validate("#", """{"v":1,"accepted":true,"commands":[]}"""));

    [Fact]
    public void HealthResponse_Valid_WithAndWithoutToken()
    {
        Assert.Empty(V.Validate("#", """{"v":1,"ok":true,"server_time":"2024-08-07T15:02:11Z","server_version":"0.1.0","commands":[]}"""));
        Assert.Empty(V.Validate("#", """{"v":1,"ok":true,"server_time":"2024-08-07T15:02:11Z","server_version":"0.1.0","source_id":"src_x","scopes":["ingest"],"commands":[]}"""));
    }

    [Fact]
    public void ErrorEnvelope_Valid_WithSupported()
        => Assert.Empty(V.Validate("#", """{"v":1,"error":{"code":"unsupported_version","message":"x","supported":[1]},"commands":[]}"""));

    [Fact]
    public void ErrorEnvelope_Valid_WithoutSupported()
        => Assert.Empty(V.Validate("#", """{"v":1,"error":{"code":"token_invalid","message":"x"},"commands":[]}"""));
}

public class ProtocolValidationTests
{
    [Theory]
    [InlineData("", RejectionReasons.BadRecordId)]
    [InlineData("   ", RejectionReasons.BadRecordId)]
    public void RecordId_Rules(string recordId, string reason)
    {
        var (_, rejection) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = recordId, DedupScope = "System", Time = "2024-08-07T15:02:11Z" },
            SourceKinds.WindowsAgent);
        Assert.Equal(reason, rejection!.Reason);
    }

    [Fact]
    public void RecordId_Over128Chars_Rejected()
    {
        var (_, rejection) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = new string('x', 129), DedupScope = "System", Time = "2024-08-07T15:02:11Z" },
            SourceKinds.WindowsAgent);
        Assert.Equal(RejectionReasons.BadRecordId, rejection!.Reason);
    }

    [Fact]
    public void DedupScope_Null_Rejected()
    {
        var (_, rejection) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", Time = "2024-08-07T15:02:11Z" },
            SourceKinds.WindowsAgent);
        Assert.Equal(RejectionReasons.BadDedupScope, rejection!.Reason);
    }

    [Fact]
    public void DedupScope_EmptyString_Accepted()
    {
        var (item, rejection) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "7", DedupScope = "", Time = "2024-08-07T15:02:11Z" },
            SourceKinds.SyslogFeed);
        Assert.Null(rejection);
        Assert.Equal("7", item!.RecordId);
    }

    [Theory]
    [InlineData("2024-08-07T15:02:11Z", true)]
    [InlineData("2024-08-07T15:02:11.123Z", true)]
    [InlineData("2024-08-07T15:02:11.123456789Z", true)]
    [InlineData("2024-08-07T15:02:11", false)]
    [InlineData("2024-08-07 15:02:11Z", false)]
    [InlineData("2024-08-07T15:02:11+02:00", false)]
    [InlineData("not-a-time", false)]
    public void Time_Rules(string time, bool valid)
    {
        var (item, rejection) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "S", Time = time },
            SourceKinds.WindowsAgent);
        Assert.Equal(valid, rejection is null);
        if (valid)
        {
            var expected = DateTimeOffset.Parse(time);
            Assert.Equal(expected, item!.Time);
            Assert.Equal(TimeSpan.Zero, item.Time.Offset); // always UTC
        }
    }

    [Fact]
    public void Severity_Range_DependsOnSourceKind()
    {
        // Windows: 1..5; 6 is invalid for windows-agent.
        var (_, r1) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "S", Time = "2024-08-07T15:02:11Z", Severity = 6 },
            SourceKinds.WindowsAgent);
        Assert.Equal(RejectionReasons.Schema, r1!.Reason);
        // syslog-feed: 0..7; 6 is valid, 8 is not.
        var (i2, r2) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "", Time = "2024-08-07T15:02:11Z", Severity = 6 },
            SourceKinds.SyslogFeed);
        Assert.Null(r2);
        Assert.Equal(6, i2!.Severity);
        var (_, r3) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "", Time = "2024-08-07T15:02:11Z", Severity = 8 },
            SourceKinds.SyslogFeed);
        Assert.Equal(RejectionReasons.Schema, r3!.Reason);
        // Absent severity is allowed (Level 0 is omitted, PROTOCOL §10) and is
        // defaulted at ingest: Windows Information for windows-agent.
        var (i4, r4) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "S", Time = "2024-08-07T15:02:11Z" },
            SourceKinds.WindowsAgent);
        Assert.Null(r4);
        Assert.Equal(4, i4!.Severity!.Value);
        // syslog-feed default is RFC 5424 informational (6).
        var (i5, r5) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "", Time = "2024-08-07T15:02:11Z" },
            SourceKinds.SyslogFeed);
        Assert.Null(r5);
        Assert.Equal(6, i5!.Severity!.Value);
    }

    [Fact]
    public void MessageAndRaw_SizeCaps()
    {
        var (_, r1) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "S", Time = "2024-08-07T15:02:11Z", Message = new string('x', 64 * 1024 + 1) },
            SourceKinds.WindowsAgent);
        Assert.Equal(RejectionReasons.MessageOversize, r1!.Reason);

        var (_, r2) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "S", Time = "2024-08-07T15:02:11Z", Raw = new string('x', 16 * 1024 + 1) },
            SourceKinds.WindowsAgent);
        Assert.Equal(RejectionReasons.RawOversize, r2!.Reason);
    }

    [Fact]
    public void Fields_StringCap_Rejected()
    {
        var fields = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["big"] = new string('y', 64 * 1024 + 1) });
        var (_, rejection) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "1", DedupScope = "S", Time = "2024-08-07T15:02:11Z", Fields = fields },
            SourceKinds.WindowsAgent);
        Assert.Equal(RejectionReasons.FieldOversize, rejection!.Reason);
    }

    [Fact]
    public void PromotedFields_MapToColumns()
    {
        var fields = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["channel"] = "System",
            ["event_id"] = 6008L,
            ["task"] = 0L,
            ["opcode"] = 0L,
            ["keywords"] = "0x80000000000000",
            ["provider_guid"] = "{abc}",
            ["event_data"] = new Dictionary<string, object?> { ["LogonType"] = 10L },
        });
        var (item, rejection) = ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "e1:5", DedupScope = "System", Time = "2024-08-07T15:02:11Z", Fields = fields },
            SourceKinds.WindowsAgent);
        Assert.Null(rejection);
        Assert.Equal("System", item!.Channel);
        Assert.Equal(6008, item.EventId);
        Assert.Equal("0x80000000000000", item.Keywords);
        Assert.Contains("\"event_data\"", item.FieldsJson);
    }

    [Fact]
    public void EpochRecordIds_AreOpaqueAndDistinct()
    {
        Assert.Equal(RejectionReasons.BadRecordId, ProtocolValidation.ValidateLogItem(
            new LogItemDto { Kind = "log", RecordId = "", DedupScope = "S", Time = "2024-08-07T15:02:11Z" }, SourceKinds.WindowsAgent).Rejection!.Reason);
        // "41235", "e1:41235", "e2:41235" are distinct opaque strings.
        foreach (var id in new[] { "41235", "e1:41235", "e2:41235" })
        {
            var (item, rejection) = ProtocolValidation.ValidateLogItem(
                new LogItemDto { Kind = "log", RecordId = id, DedupScope = "System", Time = "2024-08-07T15:02:11Z" }, SourceKinds.WindowsAgent);
            Assert.Null(rejection);
            Assert.Equal(id, item!.RecordId);
        }
    }

    [Fact]
    public void ParseLogItem_TypeMismatches_ArePerItemSchemaRejections()
    {
        // A field of the wrong JSON type must reject the item per-item with
        // "schema", never fail the whole batch at deserialization (PROTOCOL §6.2/§6.4).
        var badFacility = JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"log","record_id":"1","dedup_scope":"S","time":"2024-08-07T15:02:11Z","facility":123}""");
        Assert.Equal(RejectionReasons.Schema, ProtocolValidation.ParseLogItem(badFacility, SourceKinds.WindowsAgent).Rejection!.Reason);

        var badSeverity = JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"log","record_id":"1","dedup_scope":"S","time":"2024-08-07T15:02:11Z","severity":"3"}""");
        Assert.Equal(RejectionReasons.Schema, ProtocolValidation.ParseLogItem(badSeverity, SourceKinds.WindowsAgent).Rejection!.Reason);

        var badRecordId = JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"log","record_id":123,"dedup_scope":"S","time":"2024-08-07T15:02:11Z"}""");
        Assert.Equal(RejectionReasons.Schema, ProtocolValidation.ParseLogItem(badRecordId, SourceKinds.WindowsAgent).Rejection!.Reason);

        var badTime = JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"log","record_id":"1","dedup_scope":"S","time":42}""");
        Assert.Equal(RejectionReasons.Schema, ProtocolValidation.ParseLogItem(badTime, SourceKinds.WindowsAgent).Rejection!.Reason);

        var notObject = JsonSerializer.Deserialize<JsonElement>("\"nope\"");
        Assert.Equal(RejectionReasons.Schema, ProtocolValidation.ParseLogItem(notObject, SourceKinds.WindowsAgent).Rejection!.Reason);

        // JSON null preserves the documented per-reason semantics (PROTOCOL §11.4).
        var nullScope = JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"log","record_id":"1","dedup_scope":null,"time":"2024-08-07T15:02:11Z"}""");
        Assert.Equal(RejectionReasons.BadDedupScope, ProtocolValidation.ParseLogItem(nullScope, SourceKinds.WindowsAgent).Rejection!.Reason);

        var nullSeverity = JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"log","record_id":"1","dedup_scope":"S","time":"2024-08-07T15:02:11Z","severity":null}""");
        Assert.Null(ProtocolValidation.ParseLogItem(nullSeverity, SourceKinds.WindowsAgent).Rejection);

        // A well-formed item parses and maps promoted fields.
        var ok = JsonSerializer.Deserialize<JsonElement>(
            """{"kind":"log","record_id":"e1:5","dedup_scope":"System","time":"2024-08-07T15:02:11.123Z","severity":3,"facility":"X","message":"m","fields":{"channel":"System","event_id":6008},"raw":"<Event/>"}""");
        var (item, rejection) = ProtocolValidation.ParseLogItem(ok, SourceKinds.WindowsAgent);
        Assert.Null(rejection);
        Assert.Equal("e1:5", item!.RecordId);
        Assert.Equal("System", item.Channel);
        Assert.Equal(6008, item.EventId);
        Assert.Equal(3, item.Severity!.Value);
    }

    [Fact]
    public void ParseTelemetryItem_Heartbeat()
    {
        var el = JsonSerializer.Deserialize<JsonElement>("""{"kind":"heartbeat","sent_at":"2024-08-07T15:02:11Z","boot_time":"2024-08-01T00:00:00Z","degraded":"spool_full","counters":{"a":1}}""");
        var parsed = ProtocolValidation.ParseTelemetryItem(el, out var error);
        Assert.Null(error);
        var hb = Assert.IsType<HeartbeatPayload>(parsed);
        Assert.Equal(DateTimeOffset.Parse("2024-08-07T15:02:11Z"), hb.SentAt);
        Assert.Equal("spool_full", hb.Degraded);
        Assert.NotNull(hb.CountersJson);
    }

    [Fact]
    public void ParseTelemetryItem_Facts()
    {
        var el = JsonSerializer.Deserialize<JsonElement>("""{"kind":"facts","collected_at":"2024-08-07T15:02:10Z","stale":true,"vms":[]}""");
        var parsed = ProtocolValidation.ParseTelemetryItem(el, out var error);
        Assert.Null(error);
        var f = Assert.IsType<FactsPayload>(parsed);
        Assert.True(f.Stale);
        Assert.Empty(f.Vms);
    }
}
