using System.Text;
using Hyveman.Server.Certificates;
using Hyveman.Server.Config;
using Microsoft.AspNetCore.Http;

namespace Hyveman.Server.Tests.Certificates;

/// <summary>
/// AcmeHttpMiddleware: http-01 challenge serving (200/404), http→https 308 redirect
/// (including a non-default https port from urls), and pass-through for TLS requests.
/// </summary>
public sealed class AcmeHttpMiddlewareTests
{
    private static ServerOptions Options(string urls = "https://0.0.0.0:443")
        => new() { Urls = urls };

    private static async Task<(int status, string body, string? location)> RunAsync(
        Http01ChallengeStore store, ServerOptions options, Action<DefaultHttpContext> setup)
    {
        var nextCalled = false;
        var middleware = new AcmeHttpMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; }, store, options);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        setup(context);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
        return (context.Response.StatusCode, body, context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task KnownChallengeToken_ServesKeyAuthorization()
    {
        var store = new Http01ChallengeStore();
        store.Set("tok_1", "tok_1.keyAuthz");

        var (status, body, _) = await RunAsync(store, Options(), ctx =>
        {
            ctx.Request.Scheme = "http";
            ctx.Request.Path = "/.well-known/acme-challenge/tok_1";
        });

        Assert.Equal(200, status);
        Assert.Equal("tok_1.keyAuthz", body);
    }

    [Fact]
    public async Task UnknownChallengeToken_Returns404()
    {
        var store = new Http01ChallengeStore();
        store.Set("tok_1", "value");

        var (status, body, _) = await RunAsync(store, Options(), ctx =>
        {
            ctx.Request.Scheme = "http";
            ctx.Request.Path = "/.well-known/acme-challenge/other-token";
        });

        Assert.Equal(404, status);
        Assert.Equal("", body);
    }

    [Fact]
    public async Task MalformedChallengePath_Returns404()
    {
        var store = new Http01ChallengeStore();
        store.Set("tok_1", "value");

        // Trailing slash / extra segment → not a valid token lookup.
        var (status, _, _) = await RunAsync(store, Options(), ctx =>
        {
            ctx.Request.Scheme = "http";
            ctx.Request.Path = "/.well-known/acme-challenge/tok_1/";
        });

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task PlainHttp_RedirectsToHttps_KeepingPathAndQuery()
    {
        var store = new Http01ChallengeStore();

        var (status, _, location) = await RunAsync(store, Options(), ctx =>
        {
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("hyveman.example.com", 80);
            ctx.Request.Path = "/dashboard";
            ctx.Request.QueryString = new QueryString("?tab=alerts");
        });

        Assert.Equal(308, status);
        Assert.Equal("https://hyveman.example.com/dashboard?tab=alerts", location);
    }

    [Fact]
    public async Task PlainHttp_RedirectUsesConfiguredHttpsPort()
    {
        var store = new Http01ChallengeStore();

        var (status, _, location) = await RunAsync(store, Options("https://0.0.0.0:8443"), ctx =>
        {
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("hyveman.example.com", 80);
            ctx.Request.Path = "/health";
        });

        Assert.Equal(308, status);
        Assert.Equal("https://hyveman.example.com:8443/health", location);
    }

    [Fact]
    public async Task HttpsRequest_PassesThrough()
    {
        var store = new Http01ChallengeStore();

        var (status, _, _) = await RunAsync(store, Options(), ctx =>
        {
            ctx.Request.Scheme = "https";
            ctx.Request.Path = "/dashboard";
        });

        Assert.Equal(200, status);   // the test's next delegate answers 200
    }

    [Fact]
    public async Task HttpsChallengeRequest_IsAlsoServed()
    {
        var store = new Http01ChallengeStore();
        store.Set("tok_1", "value");

        var (status, body, _) = await RunAsync(store, Options(), ctx =>
        {
            ctx.Request.Scheme = "https";
            ctx.Request.Path = "/.well-known/acme-challenge/tok_1";
        });

        Assert.Equal(200, status);
        Assert.Equal("value", body);
    }
}
