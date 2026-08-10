using System.Text.Json;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>Maps heartbeat host-state payloads (PROTOCOL §7.1, AGENT §8) into the
/// host `metrics` time series so they participate in threshold rules (DESIGN
/// §4.4 rule type 4) and host metrics reporting (DESIGN §5.2). Series:
///   - `disk_free:<path>`      — free bytes per fixed volume, unit "B"
///   - `disk_free_pct:<path>`  — free share 0–100, unit "%"
///   - `mem_available`         — Windows available RAM (free + standby), unit "B"
///   - `mem_available_pct`     — available share 0–100, unit "%"
/// The wire pct values are fractions (0.23); the metrics store 0–100 for display.</summary>
public static class HeartbeatMetrics
{
    public static IReadOnlyList<MetricRecord> FromHeartbeat(string hostId, HeartbeatPayload hb, DateTimeOffset time)
    {
        var result = new List<MetricRecord>();

        if (hb.MemAvailableBytes is { } avail)
        {
            result.Add(new MetricRecord(hostId, "mem_available", avail, "B", time));
            if (hb.MemTotalBytes is { } total && total > 0)
                result.Add(new MetricRecord(hostId, "mem_available_pct", Math.Round(avail * 100.0 / total, 2), "%", time));
        }

        if (string.IsNullOrEmpty(hb.FreeDiskJson)) return result;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(hb.FreeDiskJson); }
        catch (JsonException) { return result; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var vol in doc.RootElement.EnumerateArray())
            {
                if (vol.ValueKind != JsonValueKind.Object) continue;
                if (!vol.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) continue;
                var path = p.GetString();
                if (string.IsNullOrEmpty(path)) continue;

                if (vol.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number)
                {
                    if (b.TryGetInt64(out var bytes))
                        result.Add(new MetricRecord(hostId, $"disk_free:{path}", bytes, "B", time));
                    else if (b.TryGetDouble(out var bytesD))
                        result.Add(new MetricRecord(hostId, $"disk_free:{path}", bytesD, "B", time));
                }
                if (vol.TryGetProperty("pct", out var pc) && pc.ValueKind == JsonValueKind.Number
                    && pc.TryGetDouble(out var pct))
                {
                    result.Add(new MetricRecord(hostId, $"disk_free_pct:{path}", Math.Round(pct * 100, 2), "%", time));
                }
            }
        }
        return result;
    }
}
