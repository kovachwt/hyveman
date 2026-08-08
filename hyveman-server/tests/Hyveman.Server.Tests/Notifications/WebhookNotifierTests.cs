using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hyveman.Server.Config;
using Hyveman.Server.Notifications;
using Hyveman.Server.Tests.TestInfra;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyveman.Server.Tests.Notifications;

/// <summary>Generic webhook notifier (§10.1): SSRF guard, bearer secret, response classification.</summary>
public sealed class WebhookNotifierTests
{
    private const string PublicUrl = "https://8.8.8.8/hook"; // public IP literal: no DNS needed, passes the guard

    private static WebhookNotifier Build(StubHttpMessageHandler handler, ServerOptions? opts = null)
        => new(new FakeHttpClientFactory(handler), opts ?? new ServerOptions(),
            NullLogger<WebhookNotifier>.Instance);

    private static ChannelConfig WebhookCfg(string? url = PublicUrl, string? secret = null) => new(
        "Webhook", "webhook", null, null, url, secret);

    private static Notification Sample() => new(
        "alert_1", "Agent silent", "heartbeat", "warning",
        "HOST01", "no heartbeat for 300s", "2025-01-01T00:00:00Z", 1, "");

    [Fact]
    public async Task MissingUrl_IsPermanentFailure()
    {
        var notifier = Build(new StubHttpMessageHandler());
        var result = await notifier.SendAsync(Sample(), WebhookCfg(url: null), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Permanent);
    }

    [Fact]
    public async Task SsrfBlockedTarget_IsPermanentFailure()
    {
        var handler = new StubHttpMessageHandler();
        var notifier = Build(handler);

        var result = await notifier.SendAsync(Sample(), WebhookCfg(url: "http://127.0.0.1:8080/hook"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Permanent);
        Assert.Contains("blocked", result.Error);
        Assert.Empty(handler.Requests); // never sent
    }

    [Fact]
    public async Task Success_PostsJsonPayloadWithBearerSecret()
    {
        var handler = new StubHttpMessageHandler();
        var notifier = Build(handler);

        var result = await notifier.SendAsync(Sample(), WebhookCfg(secret: "s3cret"), CancellationToken.None);

        Assert.True(result.Success);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(PublicUrl, req.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer s3cret", req.Headers.GetValues("Authorization").Single());

        var body = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        Assert.Equal("alert_1", body.GetProperty("alert_id").GetString());
        Assert.Equal("warning", body.GetProperty("severity").GetString());
        Assert.Equal("HOST01", body.GetProperty("host").GetString());
        Assert.Equal("Agent silent", body.GetProperty("rule").GetString());
    }

    [Fact]
    public async Task Success_WithoutSecret_HasNoAuthHeader()
    {
        var handler = new StubHttpMessageHandler();
        var notifier = Build(handler);

        await notifier.SendAsync(Sample(), WebhookCfg(), CancellationToken.None);

        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthFailure_IsPermanent(HttpStatusCode code)
    {
        var handler = new StubHttpMessageHandler { Responder = _ => new HttpResponseMessage(code) };
        var result = await Build(handler).SendAsync(Sample(), WebhookCfg(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Permanent);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherHttpErrors_AreTransient(HttpStatusCode code)
    {
        var handler = new StubHttpMessageHandler { Responder = _ => new HttpResponseMessage(code) };
        var result = await Build(handler).SendAsync(Sample(), WebhookCfg(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Permanent);
    }

    [Fact]
    public async Task NetworkFailure_IsTransient()
    {
        var handler = new StubHttpMessageHandler { ThrowOnNext = true };
        var result = await Build(handler).SendAsync(Sample(), WebhookCfg(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Permanent);
        Assert.Contains("connection refused", result.Error);
    }
}
