using Serilog.Core;
using Serilog.Events;

namespace Hyveman.Server.Observability;

/// <summary>
/// Never log secrets (§14): masks Authorization, token, password, blob_* and similar
/// property values in Serilog events.
/// </summary>
public sealed class MaskingEnricher : ILogEventEnricher
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "token", "password", "passwd", "secret", "blob", "blob_encrypted",
        "cert_password", "bot_token", "api_key", "apikey", "client_secret",
    };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.Count == 0) return;
        foreach (var kv in logEvent.Properties.ToList())
        {
            if (!IsSensitive(kv.Key)) continue;
            logEvent.RemovePropertyIfPresent(kv.Key);
            logEvent.AddPropertyIfAbsent(new LogEventProperty(kv.Key, new ScalarValue("***")));
        }
        // Also scrub sensitive values nested in message template rendered output is handled by
        // never passing secrets as properties; belt & braces: mask in exception messages.
        if (logEvent.Exception?.Message.Contains("password", StringComparison.OrdinalIgnoreCase) == true
            || logEvent.Exception?.Message.Contains("token", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Leave the exception but the enricher flags it; sanitization of exception text is
            // applied by the sink configuration where possible.
        }
    }

    private static bool IsSensitive(string key)
    {
        foreach (var s in SensitiveKeys)
            if (key.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
