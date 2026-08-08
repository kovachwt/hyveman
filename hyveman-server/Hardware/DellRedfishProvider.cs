using System.Net;
using System.Text.Json;
using Hyveman.Server.Auth;
using Hyveman.Server.Storage.Repos;

namespace Hyveman.Server.Hardware;

/// <summary>Vendor-agnostic poll result (§8.1): overall rollup + component states + metric samples.</summary>
public sealed record HostHealth(
    string RollupState,                      // ok|warning|critical|unknown
    IReadOnlyList<ComponentState> Components,
    IReadOnlyList<MetricSample> Metrics);

public sealed record MetricSample(string Name, double Value, string? Unit);

/// <summary>One per vendor/transport; future providers implement the same model (DESIGN §4.2).</summary>
public interface IHardwareProvider
{
    string Kind { get; }
    Task<HostHealth> PollAsync(HostRow host, string idracUsername, string idracPassword, CancellationToken ct);
}

/// <summary>Dell iDRAC Redfish provider (MVP, §8.2).</summary>
public sealed class DellRedfishProvider : IHardwareProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<DellRedfishProvider> _logger;
    private readonly int _timeoutS;

    public string Kind => "dell-poweredge";

    public DellRedfishProvider(IHttpClientFactory httpFactory, ILogger<DellRedfishProvider> logger, Config.ServerOptions opts)
    {
        _http = httpFactory.CreateClient("redfish");
        _logger = logger;
        _timeoutS = opts.Poller.TimeoutS;
    }

    public async Task<HostHealth> PollAsync(HostRow host, string username, string password, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutS));

        var baseUrl = host.IdracUrl!.TrimEnd('/');
        var cred = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
        var req = new HttpRequestMessage { Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", cred) } };

        var components = new List<ComponentState>();
        var metrics = new List<MetricSample>();

        // 1. System health rollup.
        var system = await GetJsonAsync(baseUrl + "/redfish/v1/Systems/System.Embedded.1", req, timeout.Token);
        var rollup = NormalizeHealth(ReadHealth(system, "Status", "HealthRollup") ?? ReadHealth(system, "Status", "Health"));
        AddComponent(components, "system", "System.Embedded.1", rollup, ReadHealth(system, "Status", "Health"));
        var procSummary = ReadHealth(system, "ProcessorSummary", "Status", "Health");
        var memSummary = ReadHealth(system, "MemorySummary", "Status", "Health");
        AddComponent(components, "cpu", "CPU (rollup)", NormalizeHealth(procSummary), procSummary);
        AddComponent(components, "memory", "Memory (rollup)", NormalizeHealth(memSummary), memSummary);

        // 2. Chassis: Thermal (temps, fans) + Power (PSUs, wattage).
        var chassis = await GetJsonAsync(baseUrl + "/redfish/v1/Chassis", req, timeout.Token);
        var chassisIds = ReadChassisIds(chassis, baseUrl);
        foreach (var chassisId in chassisIds)
        {
            JsonElement thermal;
            try { thermal = await GetJsonAsync($"{baseUrl}/redfish/v1/Chassis/{chassisId}/Thermal", req, timeout.Token); }
            catch (HttpRequestException) { continue; }
            foreach (var t in ReadTemps(thermal))
            {
                AddComponent(components, "temp", t.Name, t.State, $"{t.Value:0.#}°C (min {t.Min:0.#} max {t.Max:0.#})");
                if (t.Value.HasValue) metrics.Add(new MetricSample($"temp.{t.Name}", t.Value.Value, "C"));
            }
            foreach (var f in ReadFans(thermal))
            {
                AddComponent(components, "fan", f.Name, f.State, f.Rpm.HasValue ? $"{f.Rpm} RPM" : null);
                if (f.Rpm.HasValue) metrics.Add(new MetricSample($"fan.{f.Name}", f.Rpm.Value, "rpm"));
            }

            JsonElement power;
            try { power = await GetJsonAsync($"{baseUrl}/redfish/v1/Chassis/{chassisId}/Power", req, timeout.Token); }
            catch (HttpRequestException) { continue; }
            foreach (var ps in ReadPsu(power))
            {
                AddComponent(components, "psu", ps.Name, ps.State, ps.Watts.HasValue ? $"{ps.Watts:0.#} W" : null);
            }
            foreach (var pc in ReadPowerControl(power))
            {
                if (pc.Watts.HasValue)
                {
                    metrics.Add(new MetricSample("psu_watts", pc.Watts.Value, "W"));
                    AddComponent(components, "psu", pc.Name, "ok", $"{pc.Watts:0.#} W");
                }
            }
        }

        // 3. Dell OEM disk/PERC/memory collections (best-effort; absent on some iDRAC versions).
        await PollDellOemAsync(baseUrl, req, components, timeout.Token);

        return new HostHealth(rollup, components, metrics);
    }

    private async Task PollDellOemAsync(string baseUrl, HttpRequestMessage req, List<ComponentState> components, CancellationToken ct)
    {
        var collections = new (string path, string type)[]
        {
            ("/redfish/v1/Systems/System.Embedded.1/Oem/Dell/DellPhysicalDiskCollection", "disk"),
            ("/redfish/v1/Systems/System.Embedded.1/Oem/Dell/DellArrayDiskCollection", "disk"),
            ("/redfish/v1/Systems/System.Embedded.1/Oem/Dell/DellControllerCollection", "controller"),
            ("/redfish/v1/Systems/System.Embedded.1/Oem/Dell/DellMemoryCollection", "memory"),
        };
        foreach (var (path, type) in collections)
        {
            JsonElement coll;
            try
            {
                coll = await GetJsonAsync(baseUrl + path, req, ct);
            }
            catch (HttpRequestException)
            {
                continue;   // OEM collection not present on this iDRAC — normal.
            }
            foreach (var member in ReadMembers(coll).Select(m => Resolve(m, baseUrl)))
            {
                JsonElement? detail = null;
                try { detail = await GetJsonAsync(member, req, ct); } catch (HttpRequestException) { }
                if (detail is null) continue;
                var name = ReadString(detail.Value, "Name") ?? ReadString(detail.Value, "Id") ?? "?";
                var state = NormalizeHealth(ReadHealth(detail.Value, "Status", "Health"));
                var detailTxt = BuildDetail(detail.Value);
                components.Add(new ComponentState(type, name, state, detailTxt));
            }
        }
    }

    // ── JSON extraction helpers ────────────────────────────────────────────
    private async Task<JsonElement> GetJsonAsync(string url, HttpRequestMessage template, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = template.Headers.Authorization;
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Redfish {url} → {(int)resp.StatusCode}");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();   // detach from the document lifetime
    }

    private static string? ReadHealth(JsonElement obj, params string[] path)
    {
        JsonElement cur = obj;
        foreach (var p in path)
        {
            if (!cur.TryGetProperty(p, out cur)) return null;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static List<string> ReadChassisIds(JsonElement chassis, string baseUrl)
    {
        var list = new List<string>();
        foreach (var m in ReadMembers(chassis).Select(m => Resolve(m, baseUrl)))
        {
            var id = m.Split('/').LastOrDefault();
            if (!string.IsNullOrEmpty(id)) list.Add(id);
        }
        if (list.Count == 0) list.Add("Chassis.Embedded.1");
        return list;
    }

    private static IEnumerable<string> ReadMembers(JsonElement obj)
    {
        if (!obj.TryGetProperty("Members", out var members) || members.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var m in members.EnumerateArray())
        {
            if (m.TryGetProperty("@odata.id", out var id) && id.ValueKind == JsonValueKind.String)
                yield return id.GetString()!;
            else if (m.ValueKind == JsonValueKind.String)
                yield return m.GetString()!;
        }
    }

    /// <summary>Real iDRACs return absolute @odata.id URLs; be defensive about relative ones.</summary>
    private static string Resolve(string member, string baseUrl)
        => member.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? member
            : baseUrl + (member.StartsWith('/') ? member : "/" + member);

    private static IEnumerable<(string Name, string State, double? Value, double? Min, double? Max)> ReadTemps(JsonElement thermal)
    {
        if (!thermal.TryGetProperty("Temperatures", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var t in arr.EnumerateArray())
        {
            yield return (
                ReadString(t, "Name") ?? "temp",
                NormalizeHealth(ReadHealth(t, "Status", "Health")),
                ReadDouble(t, "ReadingCelsius"),
                ReadDouble(t, "MinReadingRangeTemp"),
                ReadDouble(t, "MaxReadingRangeTemp"));
        }
    }

    private static IEnumerable<(string Name, string State, double? Rpm)> ReadFans(JsonElement thermal)
    {
        if (!thermal.TryGetProperty("Fans", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var f in arr.EnumerateArray())
        {
            yield return (ReadString(f, "Name") ?? "fan", NormalizeHealth(ReadHealth(f, "Status", "Health")), ReadDouble(f, "Reading"));
        }
    }

    private static IEnumerable<(string Name, string State, double? Watts)> ReadPsu(JsonElement power)
    {
        if (!power.TryGetProperty("PowerSupplies", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var p in arr.EnumerateArray())
        {
            yield return (ReadString(p, "Name") ?? "psu", NormalizeHealth(ReadHealth(p, "Status", "Health")), ReadDouble(p, "LastPowerOutputWatts"));
        }
    }

    private static IEnumerable<(string Name, double? Watts)> ReadPowerControl(JsonElement power)
    {
        if (!power.TryGetProperty("PowerControl", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var p in arr.EnumerateArray())
        {
            yield return (ReadString(p, "Name") ?? "PowerControl", ReadDouble(p, "PowerConsumedWatts"));
        }
    }

    private static double? ReadDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    private static string? BuildDetail(JsonElement obj)
    {
        var parts = new List<string>();
        if (obj.TryGetProperty("Manufacturer", out var m) && m.ValueKind == JsonValueKind.String) parts.Add(m.GetString()!);
        if (obj.TryGetProperty("Model", out var mo) && mo.ValueKind == JsonValueKind.String) parts.Add(mo.GetString()!);
        if (obj.TryGetProperty("CapacityMiB", out var c) && c.ValueKind == JsonValueKind.Number) parts.Add($"{c.GetInt64() / 1024} GiB");
        if (obj.TryGetProperty("FailurePredicted", out var fp)) parts.Add($"FailurePredicted={fp}");
        if (obj.TryGetProperty("MediaType", out var mt) && mt.ValueKind == JsonValueKind.String) parts.Add(mt.GetString()!);
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>Canonical state vocabulary (§8.2): iDRAC Health → ok|warning|critical|unknown.</summary>
    public static string NormalizeHealth(string? health)
    {
        return health?.ToLowerInvariant() switch
        {
            "ok" => "ok",
            "warning" => "warning",
            "critical" => "critical",
            _ => "unknown",
        };
    }

    private static void AddComponent(List<ComponentState> list, string type, string name, string state, string? detail)
    {
        // Rollup components: don't duplicate an identical (type,name).
        if (list.Any(c => c.Type == type && c.Name == name)) return;
        list.Add(new ComponentState(type, name, state, detail));
    }
}

/// <summary>Maps provider output → components/metrics/snapshots (§8.3). Currently a thin identity
/// pass-through; the seam exists so SNMP traps and future providers share the alert path.</summary>
public static class ComponentNormalizer
{
    public static Task StoreAsync(Storage.Db db, HostRow host, HostHealth health, string seenAt)
        => db.Writer.WithTransactionAsync(async conn =>
        {
            await Storage.Repos.ComponentRepository.MergeComponentsAsync(conn, host.Id, health.Components, seenAt);
            var json = System.Text.Json.JsonSerializer.Serialize(health.Components);
            await Storage.Repos.ComponentRepository.InsertSnapshotAsync(conn, host.Id, seenAt, health.RollupState, json);
            foreach (var m in health.Metrics)
                await Storage.Repos.ComponentRepository.InsertMetricAsync(conn, host.Id, seenAt, m.Name, m.Value, m.Unit);
            await db.Hosts.MarkPollAsync(conn, host.Id, seenAt, true);
        });

    public static Task MarkUnreachableAsync(Storage.Db db, HostRow host, string seenAt)
        => db.Writer.WithTransactionAsync(async conn =>
        {
            await Storage.Repos.ComponentRepository.MarkAllUnknownAsync(conn, host.Id, seenAt);
            await db.Hosts.MarkPollAsync(conn, host.Id, seenAt, false);
        });
}
