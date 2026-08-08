using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hyveman.Server.Notifications;
using Hyveman.Server.Tests.TestInfra;

namespace Hyveman.Server.Tests.Notifications;

/// <summary>Telegram Bot API notifier (DESIGN §13 #4): auth classification, HTML format, chunk limit.</summary>
public sealed class TelegramNotifierTests
{
    private static TelegramNotifier Build(StubHttpMessageHandler handler) => new(new FakeHttpClientFactory(handler));

    private static ChannelConfig TelegramCfg(string? token = "bot123", string? chatId = "-100123") => new(
        "Telegram", "telegram", token, chatId, null, null);

    private static Notification Sample(string message = "disk failure") => new(
        "alert_1", "Hardware component degraded", "health", "critical",
        "HOST01", message, "2025-01-01T00:00:00Z", 2, "https://hyveman.example.lan/alerts");

    [Fact]
    public async Task MissingConfig_IsPermanentFailure()
    {
        var notifier = Build(new StubHttpMessageHandler());
        var result = await notifier.SendAsync(Sample(), TelegramCfg(token: null, chatId: null), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Permanent);
        Assert.Contains("bot_token", result.Error);
    }

    [Fact]
    public async Task Success_PostsToBotApiWithExpectedPayload()
    {
        var handler = new StubHttpMessageHandler();
        var notifier = Build(handler);

        var result = await notifier.SendAsync(Sample(), TelegramCfg(), CancellationToken.None);

        Assert.True(result.Success);
        var req = Assert.Single(handler.Requests);
        Assert.StartsWith("https://api.telegram.org/botbot123/sendMessage", req.RequestUri!.AbsoluteUri);

        var body = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        Assert.Equal("-100123", body.GetProperty("chat_id").GetString());
        Assert.Equal("HTML", body.GetProperty("parse_mode").GetString());
        Assert.True(body.GetProperty("disable_web_page_preview").GetBoolean());
        Assert.Contains("🔴", body.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthFailure_IsPermanent(HttpStatusCode code)
    {
        var handler = new StubHttpMessageHandler { Responder = _ => new HttpResponseMessage(code) };
        var result = await Build(handler).SendAsync(Sample(), TelegramCfg(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Permanent);
        Assert.Contains("auth failed", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task HttpErrors_AreTransient(HttpStatusCode code)
    {
        var handler = new StubHttpMessageHandler { Responder = _ => new HttpResponseMessage(code) };
        var result = await Build(handler).SendAsync(Sample(), TelegramCfg(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Permanent);
    }

    [Fact]
    public async Task NetworkFailure_Throws_DispatcherHandlesAsTransient()
    {
        // TelegramNotifier does not catch transport errors — they bubble to the dispatcher,
        // whose SendOneAsync catch-all converts them to a transient backoff retry.
        var handler = new StubHttpMessageHandler { ThrowOnNext = true };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).SendAsync(Sample(), TelegramCfg(), CancellationToken.None));
    }

    [Fact]
    public void Format_SelectsSeverityEmojiAndEscapesHtml()
    {
        var n = Sample("disk <SMART> & failed");
        var text = TelegramNotifier.Format(n);

        Assert.Contains("🔴 CRITICAL", text);
        Assert.Contains("disk &lt;SMART&gt; &amp; failed", text);
        Assert.Contains("<b>Host:</b> HOST01", text);
        Assert.Contains("(x2)", text);
    }

    [Fact]
    public void Format_TruncatesLongMessagesAt500Chars()
    {
        var text = TelegramNotifier.Format(Sample(new string('x', 5000)));
        Assert.Contains(new string('x', 500), text);
        Assert.Contains("…", text);
        Assert.DoesNotContain(new string('x', 501), text);
    }

    [Fact]
    public void Format_IsNeverLongerThanTelegramLimit()
    {
        // Message is truncated at 500 chars before embedding, so a formatted notification
        // can never approach the 4096-char API limit (the split loop is defensive only).
        var text = TelegramNotifier.Format(Sample(new string('x', 5000)));
        Assert.True(text.Length < 4096);
    }
}
