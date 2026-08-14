using System.Net;
using System.Text;
using System.Text.Json;
using Hyveman.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Hyveman.Tests.Api;

/// <summary>Edge cases from the PROTOCOL.md review (PROTOCOL §3/§9.1): a
/// non-integer body v must be 400 invalid_request (never 500), and
/// Content-Type is required when a body is present.</summary>
[Collection("api")]
public class EdgeCaseContractTests
{
    private readonly ApiFixture _fx;

    public EdgeCaseContractTests(ApiFixture fx) => _fx = fx;

    private Task<HttpResponseMessage> Post(string path, string token, string body, bool setContentType = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Add("X-Hyveman-Protocol", "1");
        if (token is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (setContentType)
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return _fx.Client.SendAsync(req);
    }

    private static async Task<string> Code(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() ?? "?" : "?";
    }

    [Fact]
    public async Task NonIntegerBodyV_Is400InvalidRequest()
    {
        // PROTOCOL §3: a body v different from the header is 400 invalid_request.
        var (regToken, _) = await _fx.CreateRegistrationTokenAsync("windows-agent");

        var floatV = await Post("/register", regToken, """{"v":1.5,"kind":"windows-agent","hostname":"FLOATV-1"}""");
        Assert.Equal(HttpStatusCode.BadRequest, floatV.StatusCode);
        Assert.Equal("invalid_request", await Code(floatV));

        var scientificV = await Post("/register", regToken, """{"v":1e0,"kind":"windows-agent","hostname":"SCIV-1"}""");
        Assert.Equal(HttpStatusCode.BadRequest, scientificV.StatusCode);
        Assert.Equal("invalid_request", await Code(scientificV));
    }

    [Fact]
    public async Task MissingContentType_WithBody_Is400()
    {
        // PROTOCOL §9.1: Content-Type is required when a body is present.
        var token = await _fx.RegisterAgentAsync("NOCT-1");
        var resp = await Post("/ingest/logs", token, """{"v":1,"items":[{"kind":"log","record_id":"1","dedup_scope":"S","time":"2024-08-07T00:00:00Z"}]}""", setContentType: false);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_request", await Code(resp));
    }

    [Fact]
    public async Task Credentials_CheckedBeforeBodyValidation()
    {
        // SECURITY-REVIEW-2026-08-14 M2: credential gates run before the body
        // is read or parsed. An unauthenticated request is rejected with the
        // credential error regardless of body content — even a body that
        // would fail JSON parsing or the v check if it were ever read.
        var noAuth = await Post("/ingest/logs", null!, "this is not json at all");
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        Assert.Equal("token_missing", await Code(noAuth));

        var badToken = await Post("/ingest/logs", "agt_doesnotexist", "also not json");
        Assert.Equal(HttpStatusCode.Unauthorized, badToken.StatusCode);
        Assert.Equal("token_invalid", await Code(badToken));

        var noAuthRegister = await Post("/register", null!, "garbage");
        Assert.Equal(HttpStatusCode.Unauthorized, noAuthRegister.StatusCode);
        Assert.Equal("token_missing", await Code(noAuthRegister));

        var nonRegToken = await Post("/register", "agt_stillnotaregistrationtoken", "garbage");
        Assert.Equal(HttpStatusCode.Unauthorized, nonRegToken.StatusCode);
        Assert.Equal("token_invalid", await Code(nonRegToken));
    }
}
