using System.Text.Json;
using Hyveman.Protocol;
using Xunit;

namespace Hyveman.Tests.Contract;

/// <summary>
/// Golden wire-contract tests (API.md §6.8): every request/response body shape
/// the server can produce validates against docs/schemas/protocol-v1.json, and
/// the server-produced envelopes carry the required fields (v, commands) and
/// the server-version headers on version errors.
/// </summary>
public class WireContractTests
{
    private static readonly SchemaValidator Schema = ProtocolSchema.Validator;

    private static string Serialize(object dto) => ProtocolEnvelope.Serialize(dto);

    [Fact]
    public void RegisterResponse_Validates()
    {
        var body = Serialize(new RegisterResponse
        {
            V = 1,
            SourceId = "src_01HW",
            Token = "agt_01HW",
            Scopes = ["ingest"],
            IssuedAt = "2024-08-07T15:02:11Z",
            Commands = [],
        });
        Assert.Empty(Schema.Validate("#", body));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("commands").ValueKind);
    }

    [Fact]
    public void LogsResponse_Validates_WithRejections()
    {
        var body = Serialize(new LogsResponse
        {
            V = 1,
            Accepted = 12,
            Deduped = 0,
            Rejected =
            [
                new RejectedItem { RecordId = "999", DedupScope = "System", Reason = "raw_oversize", Permanent = true },
            ],
            Commands = [],
        });
        Assert.Empty(Schema.Validate("#", body));
    }

    [Fact]
    public void TelemetryResponse_Validates()
    {
        var body = Serialize(new TelemetryResponse { V = 1, Accepted = true, Commands = [] });
        Assert.Empty(Schema.Validate("#", body));
    }

    [Fact]
    public void HealthResponse_Validates()
    {
        var body = Serialize(new HealthResponse
        {
            V = 1,
            Ok = true,
            ServerTime = "2024-08-07T15:02:11Z",
            ServerVersion = "0.1.0",
            SourceId = "src_x",
            Scopes = ["ingest"],
            Commands = [],
        });
        Assert.Empty(Schema.Validate("#", body));
    }

    [Fact]
    public void ErrorEnvelope_Validates_ForAllStableCodes()
    {
        foreach (var code in new[]
        {
            "unsupported_version", "missing_version", "invalid_request", "too_many_items",
            "token_invalid", "token_revoked", "token_missing", "wrong_scope", "unknown_source",
            "token_consumed", "payload_too_large", "name_collision", "too_many_requests",
            "unavailable", "internal", "unsupported_media_type",
        })
        {
            var body = Serialize(ProtocolEnvelope.Error(code, "message"));
            Assert.Empty(Schema.Validate("#", body));
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(code, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public void VersionError_Envelope_CarriesServerVersionAndSupported()
    {
        var body = Serialize(ProtocolEnvelope.VersionError("unsupported_version", "unsupported"));
        Assert.Empty(Schema.Validate("#", body));
        using var doc = JsonDocument.Parse(body);
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal("unsupported_version", err.GetProperty("code").GetString());
        Assert.Equal([1], err.GetProperty("supported").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        // The server's version, never the client's (PROTOCOL §3).
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.NotNull(doc.RootElement.GetProperty("commands"));
    }

    [Fact]
    public void Timestamps_AreUtcZ_Always()
    {
        var ts = ProtocolVersion.FormatUtc(new DateTimeOffset(2024, 8, 7, 15, 2, 11, TimeSpan.FromHours(2)));
        Assert.Equal("2024-08-07T13:02:11.000Z", ts);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?Z$", ts);
    }
}
