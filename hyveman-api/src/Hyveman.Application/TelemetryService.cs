using System.Text.Json;
using Hyveman.Domain;
using Hyveman.Protocol;
using Microsoft.Extensions.Logging;

namespace Hyveman.Application;

/// <summary>POST /ingest/telemetry (PROTOCOL §7). Best-effort, latest-wins,
/// not idempotent, not spooled. The heartbeat ordering rule keys on
/// boot_time/sent_at; receive time is captured independently for the
/// silence timer.</summary>
public sealed class TelemetryService(
    IAgentStatusStore agentStatus,
    IHostStore hosts,
    IHealthStore health,
    IAlertEvaluator evaluator,
    IClock clock,
    ILogger<TelemetryService> log)
{
    /// <summary>Applies a parsed telemetry batch. Throws ProtocolException for a
    /// malformed item (whole-batch 4xx, PROTOCOL §7.3).</summary>
    public async Task ProcessAsync(string sourceId, IReadOnlyList<JsonElement> items, CancellationToken ct)
    {
        if (items.Count == 0)
            throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, 400, "telemetry items must not be empty");

        var receivedAt = clock.UtcNow;
        var heartbeats = new List<HeartbeatPayload>();
        var facts = new List<FactsPayload>();

        foreach (var item in items)
        {
            var parsed = ProtocolValidation.ParseTelemetryItem(item, out var error);
            switch (parsed)
            {
                case HeartbeatPayload hb: heartbeats.Add(hb); break;
                case FactsPayload f: facts.Add(f); break;
                default:
                    throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, 400, error ?? "malformed telemetry item");
            }
        }

        foreach (var hb in heartbeats)
        {
            var stored = await agentStatus.ApplyHeartbeatAsync(sourceId, hb, receivedAt, ct);
            // Corroborating hints never change identity (PROTOCOL §4.2).
            if (!string.IsNullOrEmpty(hb.Degraded))
                log.LogInformation("Source {sourceId} reports degraded={degraded}", sourceId, hb.Degraded);
            _ = stored;
        }

        var host = await hosts.GetBySourceAsync(sourceId, ct);
        foreach (var f in facts)
        {
            var stored = await agentStatus.ApplyFactsAsync(sourceId, f, receivedAt, ct);
            if (stored && host is not null)
            {
                var vms = f.Vms.Select(v => new VmRecord(
                    host.Id, v.Name, v.State, v.HeartbeatOk, v.CpuPct, v.MemMb, v.LastSeen,
                    Stale: f.Stale, CollectedAt: f.CollectedAt)).ToList();
                await health.UpsertVmsAsync(host.Id, vms, f.Stale, f.CollectedAt, ct);
            }
        }

        // Heartbeat arrival resets the agent-silent timer (API.md §6.4); the
        // monitor is notified so a silence alert can clear.
        if (heartbeats.Count > 0)
            await evaluator.OnHeartbeatSilenceChangedAsync(ruleId: null, sourceId, silent: false, receivedAt, ct);
    }
}
