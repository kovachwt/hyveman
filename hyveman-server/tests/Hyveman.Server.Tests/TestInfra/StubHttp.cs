using System.Net;
using Hyveman.Server.Notifications;

namespace Hyveman.Server.Tests.TestInfra;

/// <summary>HttpMessageHandler stub that records requests and lets tests script responses.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    /// <summary>Response factory; default 200 OK.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK);

    /// <summary>All requests received (thread-safe enough for single-threaded tests).</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Bodies of captured requests (read while the content is still undisposed).</summary>
    public List<string> RequestBodies { get; } = new();

    /// <summary>When true, the next send throws HttpRequestException (simulates network failure).</summary>
    public bool ThrowOnNext { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
            RequestBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
        if (ThrowOnNext)
        {
            ThrowOnNext = false;
            throw new HttpRequestException("connection refused");
        }
        return Task.FromResult(Responder(request));
    }
}

public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly StubHttpMessageHandler _handler;
    public FakeHttpClientFactory(StubHttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler);
}

/// <summary>Scriptable INotifier for dispatcher/engine tests.</summary>
public sealed class CountingNotifier : INotifier
{
    public string Kind { get; set; } = "counting";
    public NotifyResult Result { get; set; } = new(true, false, "");
    public int Calls { get; private set; }
    public List<Notification> Received { get; } = new();

    public Task<NotifyResult> SendAsync(Notification n, ChannelConfig c, CancellationToken ct)
    {
        Calls++;
        Received.Add(n);
        return Task.FromResult(Result);
    }
}
