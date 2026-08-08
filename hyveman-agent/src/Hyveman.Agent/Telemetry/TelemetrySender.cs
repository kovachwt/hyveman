using Hyveman.Agent.Net;
using Microsoft.Extensions.Logging;

namespace Hyveman.Agent.Telemetry;

/// <summary>
/// Best-effort sender for heartbeats + facts (AGENT.md §5.1, §9.4): never
/// spooled — a missed heartbeat IS the alert signal, so replaying old ones is
/// wrong. 3 quick tries with exponential backoff + jitter, then discard
/// (the next tick resends). Latest-wins on the server.
/// </summary>
public sealed class TelemetrySender
{
    private readonly BackendClient _client;
    private readonly ILogger<TelemetrySender> _log;

    public TelemetrySender(BackendClient client, ILogger<TelemetrySender> log)
    {
        _client = client;
        _log = log;
    }

    public async Task SendAsync(object item, string? sourceId, CancellationToken ct)
    {
        var envelope = new TelemetryEnvelope
        {
            Source = sourceId,
            Items = new List<System.Text.Json.JsonElement>
            {
                System.Text.Json.JsonSerializer.SerializeToElement(item)
            }
        };
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var result = await _client.PostTelemetryAsync(body, ct).ConfigureAwait(false);
                if (result.Outcome == SendOutcome.Accepted)
                    return;

                // 4xx → discard (resends next interval anyway); 5xx/408/429 → retry.
                _log.LogDebug("Telemetry send attempt {attempt} classified {outcome}", attempt, result.Outcome);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Telemetry send attempt {attempt} failed", attempt);
            }

            if (attempt < 3)
                await Task.Delay(Pipeline.Backoff.DelayFor(attempt), ct).ConfigureAwait(false);
        }

        _log.LogDebug("Telemetry send gave up after 3 attempts (will resend next interval)");
    }
}
