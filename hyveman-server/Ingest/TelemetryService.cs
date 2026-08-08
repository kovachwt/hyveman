using System.Text.Json;
using Hyveman.Server.Common;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;

namespace Hyveman.Server.Ingest;

/// <summary>
/// POST /ingest/telemetry (§7.7, PROTOCOL §7) — latest-wins, not idempotent, not spooled.
/// A malformed item makes the batch 4xx and is discarded (resends next interval).
/// </summary>
public sealed class TelemetryService
{
    private readonly Db _db;
    private readonly Alerts.IHeartbeatSignal _heartbeatSignal;
    private readonly Observability.OwnMetrics _metrics;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(Db db, Alerts.IHeartbeatSignal heartbeatSignal, Observability.OwnMetrics metrics,
        ILogger<TelemetryService> logger)
    {
        _db = db;
        _heartbeatSignal = heartbeatSignal;
        _metrics = metrics;
        _logger = logger;
    }

    public sealed record TelemetryResult(bool Ok, int Status, string? ErrorCode, string? ErrorMessage);

    public async Task<TelemetryResult> IngestAsync(string sourceId, TelemetryRequest req)
    {
        // Corroborating identity (§4.2): body source is a hint only; the token is authoritative.
        // Log a warning when it differs (possible misconfig) but proceed with the token's identity.
        if (!string.IsNullOrEmpty(req.Source) && req.Source != sourceId)
            _logger.LogWarning("body source {Claimed} differs from token source {Actual} (possible misconfig)",
                req.Source, sourceId);

        if (req.Items is null || req.Items.Count == 0)
            return new(false, 400, "invalid_request", "items is required and must not be empty");

        foreach (var item in req.Items)
        {
            switch (item.Kind)
            {
                case "heartbeat":
                    if (string.IsNullOrEmpty(item.SentAt) || !WireTime.TryParseUtc(item.SentAt, out _))
                        return new(false, 400, "invalid_request", "heartbeat sent_at missing or not UTC ISO-8601");
                    await _db.Writer.WithTransactionAsync(conn => HeartbeatRepository.UpsertAsync(conn, new HeartbeatRow(
                        sourceId, WireTime.ToIsoMs(DateTimeOffset.Parse(item.SentAt!).ToUniversalTime()),
                        WireTime.NowMs(),
                        item.AgentVersion, item.ProtocolVersion, item.OsBuild,
                        string.IsNullOrEmpty(item.BootTime) ? null : item.BootTime,
                        item.UptimeS, item.Degraded ?? "", item.ConfigHash,
                        item.Counters is null ? null : JsonSerializer.Serialize(item.Counters),
                        item.FreeDisk is null ? null : JsonSerializer.Serialize(item.FreeDisk))));
                    _heartbeatSignal.OnHeartbeat(sourceId);
                    _metrics.Heartbeat();
                    break;

                case "facts":
                    var host = await _db.Hosts.GetBySourceIdAsync(sourceId);
                    if (host is null)
                    {
                        // Facts for a source with no host metadata: store nothing (hosts are
                        // hardware metadata; the UI creates the host row). Not an error.
                        break;
                    }
                    var vms = new List<VmState>();
                    foreach (var vm in item.Vms ?? new List<VmItem>())
                    {
                        if (string.IsNullOrEmpty(vm.Name)) continue;
                        var state = vm.State ?? "unknown";
                        if (state is not ("on" or "off" or "paused" or "saved" or "other" or "unknown"))
                            state = "unknown";
                        vms.Add(new VmState(vm.Name, state, vm.HeartbeatOk,
                            vm.LastSeen ?? item.CollectedAt ?? WireTime.Now(), vm.CpuPct, vm.MemMb));
                    }
                    await _db.Writer.WithTransactionAsync(conn => ComponentRepository.ReplaceVmsAsync(conn, host.Id, vms));
                    _metrics.FactsBatch();
                    break;

                default:
                    return new(false, 400, "invalid_request", $"unknown telemetry item kind '{item.Kind}'");
            }
        }
        return new(true, 200, null, null);
    }
}
