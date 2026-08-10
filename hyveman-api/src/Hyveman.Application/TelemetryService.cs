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

        var host = await hosts.GetBySourceAsync(sourceId, ct);
        foreach (var hb in heartbeats)
        {
            var stored = await agentStatus.ApplyHeartbeatAsync(sourceId, hb, receivedAt, ct);
            // Corroborating hints never change identity (PROTOCOL §4.2).
            if (!string.IsNullOrEmpty(hb.Degraded))
                log.LogInformation("Source {sourceId} reports degraded={degraded}", sourceId, hb.Degraded);

            // Host state rides the heartbeat (PROTOCOL §7.1): free disk per
            // volume + available RAM are mapped into the host metrics time
            // series and feed threshold rules (DESIGN §4.4 rule type 4, §5.2).
            // Same pattern as the Redfish poller — evaluate before store — and
            // same containment (DEFECTS.md D2): derived alerting/metrics must
            // never fail an accepted telemetry request, whose agent_status
            // write already committed above.
            if (stored && host is not null)
            {
                var hostMetrics = HeartbeatMetrics.FromHeartbeat(host.Id, hb, receivedAt);
                if (hostMetrics.Count > 0)
                {
                    try
                    {
                        await evaluator.OnThresholdsAsync(host.Id, hostMetrics, receivedAt, ct);
                        await health.AddMetricsAsync(host.Id, receivedAt, hostMetrics, ct);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Heartbeat metric evaluation/storage failed for source {sourceId}; heartbeat still accepted", sourceId);
                    }
                }
            }
        }

        foreach (var f in facts)
        {
            var stored = await agentStatus.ApplyFactsAsync(sourceId, f, receivedAt, ct);
            if (stored && host is not null)
            {
                var vms = f.Vms.Select(v => new VmRecord(
                    host.Id, v.Name, v.State, v.HeartbeatOk, v.CpuPct, v.MemMb, v.LastSeen,
                    Stale: f.Stale, CollectedAt: f.CollectedAt)).ToList();
                // VM heartbeat transitions (DESIGN §4.4 rule type 5) are
                // evaluated BEFORE the latest-wins upsert so the evaluator can
                // read the previous facts from the store (D3). Stale facts are
                // re-emitted old data after a WMI timeout (PROTOCOL §7.4) — not
                // a state change — so they are stored (the UI marks them stale)
                // but never evaluated. Derived alerting must never fail an
                // accepted telemetry request (DEFECTS.md D2).
                if (!f.Stale)
                {
                    try
                    {
                        await evaluator.OnVmsChangedAsync(host.Id, vms, receivedAt, ct);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "VM heartbeat evaluation failed for source {sourceId}; facts still stored", sourceId);
                    }
                }
                await health.UpsertVmsAsync(host.Id, vms, f.Stale, f.CollectedAt, ct);
            }
        }

        // Heartbeat arrival resets the agent-silent timer (API.md §6.4); the
        // monitor is notified so a silence alert can clear. Derived alerting
        // must never fail an accepted telemetry request (DEFECTS.md D2); the
        // heartbeat monitor and reconciliation pass repair state (API.md §9.3).
        if (heartbeats.Count > 0)
        {
            try
            {
                await evaluator.OnHeartbeatSilenceChangedAsync(ruleId: null, sourceId, silent: false, receivedAt, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Heartbeat silence evaluation failed for source {sourceId}; will reconcile later", sourceId);
            }
        }
    }
}
