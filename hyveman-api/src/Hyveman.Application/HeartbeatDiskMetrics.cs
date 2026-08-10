using System.Text.Json;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>Maps the heartbeat's `free_disk` array (PROTOCOL §7.1, AGENT §8)
/// into the host `metrics` time series so disk free space participates in
/// threshold rules (DESIGN §4.4 rule type 4) and host metrics reporting
/// (DESIGN §5.2). Two series per volume:
///   - `disk_free:<path>`      — absolute free bytes, unit "B"
///   - `disk_free_pct:<path>`  — free share as 0–100, unit "%"
/// The wire pct is a fraction (0.23); the metric stores 0–100 for display.</summary>
public static class HeartbeatDiskMetrics
{
    public static IReadOnlyList<MetricRecord> FromHeartbeat(string hostId, HeartbeatPayload hb, DateTimeOffset time)
    {
        var result = new List<MetricRecord>();
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
