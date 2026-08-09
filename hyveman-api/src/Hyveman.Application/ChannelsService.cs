using System.Text.Json;
using System.Text.Json.Nodes;
using Hyveman.Contracts;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>Notification channel administration (API.md §7.4): secrets are
/// encrypted into the vault immediately, read responses return metadata and a
/// redacted summary only, and test sends a clearly labeled notification.</summary>
public sealed class ChannelsService(
    INotificationChannelStore store,
    ICredentialVault vault,
    INotificationSender sender,
    IClock clock,
    IAuditStore audit)
{
    public async Task<List<ChannelDto>> ListAsync(CancellationToken ct)
    {
        var channels = await store.ListAsync(ct);
        var creds = await vault.ListAsync(ct);
        return channels.Select(c => Map(c, creds.FirstOrDefault(x => x.Id == c.ConfigRef))).ToList();
    }

    public async Task<ChannelDto?> GetAsync(string id, CancellationToken ct)
    {
        var c = await store.GetAsync(id, ct);
        if (c is null) return null;
        CredentialMeta? cred = null;
        if (c.ConfigRef is not null)
            cred = (await vault.ListAsync(ct)).FirstOrDefault(x => x.Id == c.ConfigRef);
        return Map(c, cred);
    }

    public async Task<ChannelDto> CreateAsync(ChannelInput input, string actor, CancellationToken ct)
    {
        var errors = Validate(input, requireConfig: true);
        if (errors.Count > 0) throw new ValidationProblemException(errors);
        var now = clock.UtcNow;
        var configRef = await vault.StoreAsync(input.Kind!, $"{input.Name} {input.Kind}",
            JsonSerializer.Serialize(input.Config), ct);
        var channel = new ChannelRecord("ch_" + HostsService.RandomId(18), input.Name!.Trim(), input.Kind!,
            configRef, input.Enabled ?? true, now, now, null, null, now);
        await store.CreateAsync(channel, ct);
        await audit.RecordAsync(actor, "channel.created", "notification_channel", channel.Id,
            JsonSerializer.Serialize(new { kind = channel.Kind }), now, ct);
        return (await GetAsync(channel.Id, ct))!;
    }

    public async Task<ChannelDto> PatchAsync(string id, ChannelInput input, string actor, CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct) ?? throw new NotFoundException($"channel '{id}' not found");
        if (input.UpdatedAt is { } expected && existing.UpdatedAt != expected)
            throw new ConflictException($"channel '{id}' was modified concurrently; reload and retry");
        var errors = Validate(input, requireConfig: false);
        if (errors.Count > 0) throw new ValidationProblemException(errors);
        var now = clock.UtcNow;

        var configRef = existing.ConfigRef;
        if (input.Config is not null)
        {
            // Blank secret fields mean "leave unchanged" (FRONTEND §8.5).
            var current = configRef is null ? null : await vault.LoadAsync(configRef, ct);
            var merged = MergeConfig(input.Kind ?? existing.Kind, input.Config, current);
            if (configRef is null)
                configRef = await vault.StoreAsync(existing.Kind, $"{existing.Name} {existing.Kind}", merged, ct);
            else
                await vault.UpdateAsync(configRef, merged, ct);
        }

        var updated = existing with
        {
            Name = input.Name?.Trim() ?? existing.Name,
            Enabled = input.Enabled ?? existing.Enabled,
            ConfigRef = configRef,
            Rotated = input.Config is not null ? now : existing.Rotated,
            UpdatedAt = now,
        };
        var ok = await store.UpdateAsync(updated, existing.UpdatedAt, ct);
        if (!ok) throw new ConflictException($"channel '{id}' was modified concurrently; reload and retry");
        await audit.RecordAsync(actor, "channel.updated", "notification_channel", id,
            JsonSerializer.Serialize(new { rotated = input.Config is not null }), now, ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task DeleteAsync(string id, string actor, CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct) ?? throw new NotFoundException($"channel '{id}' not found");
        await store.DeleteAsync(id, ct);
        if (existing.ConfigRef is not null)
            await vault.DeleteAsync(existing.ConfigRef, ct);
        await audit.RecordAsync(actor, "channel.deleted", "notification_channel", id, null, clock.UtcNow, ct);
    }

    public async Task<ChannelTestResult> TestAsync(string id, CancellationToken ct)
    {
        var channel = await store.GetAsync(id, ct) ?? throw new NotFoundException($"channel '{id}' not found");
        var now = clock.UtcNow;
        NotificationResult result;
        try
        {
            result = await sender.SendToChannelAsync(id,
                new NotificationMessage("Hyveman test notification", $"This is a test from Hyveman ({channel.Name}).",
                    "info", channel.Name), ct);
        }
        catch (Exception ex)
        {
            result = new NotificationResult(false, ex.Message, "exception");
        }
        await store.MarkTestResultAsync(id, result.Ok, now, ct);
        await audit.RecordAsync(null, "channel.tested", "notification_channel", id,
            JsonSerializer.Serialize(new { ok = result.Ok }), now, ct);
        return new ChannelTestResult { ChannelId = id, Ok = result.Ok, TestedAt = now, Error = result.Error };
    }

    private Dictionary<string, List<string>> Validate(ChannelInput input, bool requireConfig)
    {
        var errors = new Dictionary<string, List<string>>();
        if (input.Name is not null && string.IsNullOrWhiteSpace(input.Name)) errors["name"] = ["Name is required."];
        if (input.Kind is not null && !ChannelKinds.Known.Contains(input.Kind))
            errors["kind"] = [$"kind must be one of: {string.Join(", ", ChannelKinds.Known)}."];
        if (requireConfig && (input.Kind is null || input.Config is null))
            errors["config"] = ["Configuration with secrets is required when creating a channel."];
        if (input.Config is not null)
        {
            var cfg = input.Config;
            if (input.Kind == ChannelKinds.Telegram && (string.IsNullOrEmpty(cfg.TelegramBotToken) || string.IsNullOrEmpty(cfg.TelegramChatId)))
                errors["config"] = ["Telegram channels require botToken and chatId."];
            if (input.Kind == ChannelKinds.Webhook && string.IsNullOrEmpty(cfg.WebhookUrl))
                errors["config"] = ["Webhook channels require url."];
            if (input.Kind == ChannelKinds.Smtp && (string.IsNullOrEmpty(cfg.SmtpHost) || string.IsNullOrEmpty(cfg.SmtpTo)))
                errors["config"] = ["SMTP channels require host and to."];
        }
        return errors;
    }

    private static string MergeConfig(string kind, ChannelSecretInput input, string? currentJson)
    {
        JsonNode? cur = null;
        if (currentJson is not null)
        {
            try { cur = JsonNode.Parse(currentJson); } catch (JsonException) { cur = null; }
        }
        var node = cur as JsonObject ?? new JsonObject();
        void Set(string key, string? value, string? blankMeansKeep)
        {
            if (value is null) return;
            if (value.Length == 0 && blankMeansKeep is not null && node[key] is not null) return;
            node[key] = value;
        }
        Set("botToken", input.TelegramBotToken, "keep");
        Set("chatId", input.TelegramChatId, "keep");
        Set("url", input.WebhookUrl, "keep");
        Set("host", input.SmtpHost, "keep");
        Set("username", input.SmtpUsername, "keep");
        Set("password", input.SmtpPassword, "keep");
        Set("from", input.SmtpFrom, "keep");
        Set("to", input.SmtpTo, "keep");
        if (input.SmtpPort is { } port) node["port"] = port;
        if (input.SmtpUseTls is { } tls) node["useTls"] = tls;
        return node.ToJsonString();
    }

    private static ChannelDto Map(ChannelRecord c, CredentialMeta? cred) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Kind = c.Kind,
        Enabled = c.Enabled,
        Created = c.Created,
        Rotated = c.Rotated,
        LastTestAt = c.LastTestAt,
        LastTestOk = c.LastTestOk,
        ConfigSummary = c.Kind switch
        {
            ChannelKinds.Telegram => new() { ["chatId"] = "••••••" },
            ChannelKinds.Webhook => new() { ["url"] = "••••••" },
            ChannelKinds.Smtp => new() { ["host"] = "••••••", ["to"] = "••••••" },
            _ => [],
        },
    };
}
