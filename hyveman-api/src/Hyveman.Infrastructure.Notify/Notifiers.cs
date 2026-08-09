using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hyveman.Application;
using Microsoft.Extensions.Logging;

namespace Hyveman.Infrastructure.Notify;

/// <summary>Notification providers (DESIGN §4.4): Telegram Bot API, generic
/// HTTP webhook, best-effort SMTP. Provider secrets are loaded through the
/// vault and never written to logs.</summary>
public sealed class TelegramNotifier(IHttpClientFactory http, ILogger<TelegramNotifier> log) : INotifier
{
    public string Kind => "telegram";

    public async Task<NotificationResult> SendAsync(NotificationMessage message, string configJson, CancellationToken ct)
    {
        string botToken;
        string chatId;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            botToken = Config.Str(doc, "botToken") ?? Config.Str(doc, "telegramBotToken") ?? "";
            chatId = Config.Str(doc, "chatId") ?? Config.Str(doc, "telegramChatId") ?? "";
        }
        catch (JsonException)
        {
            return new NotificationResult(false, "telegram config malformed", "telegram");
        }
        if (botToken.Length == 0 || chatId.Length == 0)
            return new NotificationResult(false, "telegram config missing botToken/chatId", "telegram");

        try
        {
            var client = http.CreateClient("notify");
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.telegram.org/bot{botToken}/sendMessage");
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                chat_id = chatId,
                text = FormatTelegram(message),
                disable_web_page_preview = true,
            }), Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Telegram send failed: {status} {body}", (int)resp.StatusCode, Truncate(body, 200));
                return new NotificationResult(false, $"telegram http {(int)resp.StatusCode}", "telegram");
            }
            using var doc = JsonDocument.Parse(body);
            var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            return new NotificationResult(ok, ok ? null : "telegram api ok=false", "telegram");
        }
        catch (Exception ex)
        {
            log.LogWarning("Telegram send error: {error}", ex.Message);
            return new NotificationResult(false, ex.Message, "telegram");
        }
    }

    private static string FormatTelegram(NotificationMessage m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"⚠️ Hyveman: {m.Title}");
        if (!string.IsNullOrEmpty(m.Text)) sb.AppendLine(m.Text);
        sb.Append($"Severity: {m.Severity}");
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Defensive config reads for notifiers. Accepts both the normalized
/// vault keys (botToken, chatId, ...) written by MergeConfig and the raw
/// ChannelSecretInput spellings (telegramBotToken, ...) written by early
/// channel creation code, so a mismatched config yields a clean "config
/// missing" error instead of an unhandled KeyNotFoundException.</summary>
internal static class Config
{
    public static string? Str(JsonDocument doc, string key)
        => doc.RootElement.TryGetProperty(key, out var p) ? p.GetString() : null;

    public static int? Int(JsonDocument doc, string key)
    {
        if (!doc.RootElement.TryGetProperty(key, out var p)) return null;
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;
    }

    public static bool? Bool(JsonDocument doc, string key)
    {
        if (!doc.RootElement.TryGetProperty(key, out var p)) return null;
        return p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
    }
}

public sealed class WebhookNotifier(IHttpClientFactory http, ILogger<WebhookNotifier> log) : INotifier
{
    public string Kind => "webhook";

    public async Task<NotificationResult> SendAsync(NotificationMessage message, string configJson, CancellationToken ct)
    {
        string url;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            url = Config.Str(doc, "url") ?? Config.Str(doc, "webhookUrl") ?? "";
        }
        catch (JsonException)
        {
            return new NotificationResult(false, "webhook config malformed", "webhook");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return new NotificationResult(false, "webhook url invalid", "webhook");

        try
        {
            var client = http.CreateClient("notify");
            using var req = new HttpRequestMessage(HttpMethod.Post, uri);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                title = message.Title,
                text = message.Text,
                severity = message.Severity,
                channel = message.ChannelName,
                source = "hyveman",
            }), Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Webhook send failed: {status}", (int)resp.StatusCode);
                return new NotificationResult(false, $"webhook http {(int)resp.StatusCode}", "webhook");
            }
            return new NotificationResult(true, null, "webhook");
        }
        catch (Exception ex)
        {
            log.LogWarning("Webhook send error: {error}", ex.Message);
            return new NotificationResult(false, ex.Message, "webhook");
        }
    }
}

public sealed class SmtpNotifier : INotifier
{
    public string Kind => "smtp";

    public async Task<NotificationResult> SendAsync(NotificationMessage message, string configJson, CancellationToken ct)
    {
        string host, from, to;
        int port;
        bool useTls;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            host = Config.Str(doc, "host") ?? Config.Str(doc, "smtpHost") ?? "";
            from = Config.Str(doc, "from") ?? Config.Str(doc, "smtpFrom") ?? "hyveman@localhost";
            to = Config.Str(doc, "to") ?? Config.Str(doc, "smtpTo") ?? "";
            port = Config.Int(doc, "port") ?? Config.Int(doc, "smtpPort") ?? 587;
            useTls = Config.Bool(doc, "useTls") ?? Config.Bool(doc, "smtpUseTls") ?? false;
        }
        catch (JsonException)
        {
            return new NotificationResult(false, "smtp config malformed", "smtp");
        }
        if (host.Length == 0 || to.Length == 0)
            return new NotificationResult(false, "smtp config missing host/to", "smtp");

        try
        {
            using var smtp = new System.Net.Mail.SmtpClient(host, port)
            {
                EnableSsl = useTls,
            };
            var mail = new System.Net.Mail.MailMessage(from, to, $"Hyveman: {message.Title}", $"{message.Text}\n\nSeverity: {message.Severity}")
            {
                IsBodyHtml = false,
            };
            await smtp.SendMailAsync(mail, ct);
            return new NotificationResult(true, null, "smtp");
        }
        catch (Exception ex)
        {
            return new NotificationResult(false, ex.Message, "smtp");
        }
    }
}

/// <summary>Resolves a channel to its provider and sends (used by the outbox
/// dispatcher and the channel test endpoint). Secrets are decrypted only for
/// the duration of the call.</summary>
public sealed class NotificationSender(
    INotificationChannelStore channels,
    ICredentialVault vault,
    IEnumerable<INotifier> notifiers) : INotificationSender
{
    private readonly Dictionary<string, INotifier> _byKind = notifiers.ToDictionary(n => n.Kind);

    public async Task<NotificationResult> SendToChannelAsync(string channelId, NotificationMessage message, CancellationToken ct)
    {
        var channel = await channels.GetAsync(channelId, ct);
        if (channel is null) return new NotificationResult(false, "channel not found", "unknown");
        if (!channel.Enabled) return new NotificationResult(false, "channel disabled", channel.Kind);
        if (!_byKind.TryGetValue(channel.Kind, out var notifier))
            return new NotificationResult(false, $"no provider for kind '{channel.Kind}'", channel.Kind);
        if (channel.ConfigRef is null)
            return new NotificationResult(false, "channel has no stored configuration", channel.Kind);
        var config = await vault.LoadAsync(channel.ConfigRef, ct);
        if (config is null)
            return new NotificationResult(false, "channel configuration unavailable", channel.Kind);
        return await notifier.SendAsync(message, config, ct);
    }
}
