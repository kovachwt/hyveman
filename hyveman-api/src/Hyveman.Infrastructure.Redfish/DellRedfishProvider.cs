using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hyveman.Application;
using Hyveman.Domain;
using Microsoft.Extensions.Logging;

namespace Hyveman.Infrastructure.Redfish;

/// <summary>
/// Dell iDRAC Redfish provider (API.md §9.1, DESIGN §2.1/§4.2). Polls the
/// standard System/Thermal/Power resources plus Dell OEM disk/controller
/// collections and normalizes into the vendor-neutral component model. The
/// client never follows redirects and validates the scheme; credentials are
/// held only for the duration of the poll.
/// </summary>
public sealed class DellRedfishProvider(IHttpClientFactory http, ILogger<DellRedfishProvider> log) : IHardwareProvider
{
    public async Task<HardwarePollResult> PollAsync(HardwarePollTarget target, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient("redfish");
            var baseUri = new Uri(target.BaseUrl.TrimEnd('/'));
            if (baseUri.Scheme != "https")
                return new HardwarePollResult(false, DateTimeOffset.UtcNow, "unknown", [], [],
                    "iDRAC URL must use https");

            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{target.Username}:{target.Password}"));
            var system = await GetJsonAsync(client, baseUri, "redfish/v1/Systems/System.Embedded.1", auth, ct);
            if (system is null)
                return new HardwarePollResult(false, DateTimeOffset.UtcNow, "unknown", [], [],
                    "System resource not reachable");

            var components = new List<ComponentRecord>();
            var metrics = new List<MetricRecord>();
            var now = DateTimeOffset.UtcNow;
            var rollup = HealthStates.ToWire(MapHealth(system.Value, "system"));

            var processors = await GetJsonAsync(client, baseUri, "redfish/v1/Systems/System.Embedded.1/Processors", auth, ct);
            var memory = await GetJsonAsync(client, baseUri, "redfish/v1/Systems/System.Embedded.1/Memory", auth, ct);
            var thermal = await GetJsonAsync(client, baseUri, "redfish/v1/Chassis/System.Embedded.1/Thermal", auth, ct);
            var power = await GetJsonAsync(client, baseUri, "redfish/v1/Chassis/System.Embedded.1/Power", auth, ct);
            var chassis = await GetJsonAsync(client, baseUri, "redfish/v1/Chassis/System.Embedded.1", auth, ct);

            var members = (JsonElement? collection, string type) =>
            {
                if (collection is not { } c || !c.TryGetProperty("Members", out var ms)) return;
                foreach (var m in ms.EnumerateArray())
                {
                    if (!m.TryGetProperty("Name", out var nameProp)) continue;
                    var name = nameProp.GetString() ?? "unknown";
                    var state = MapHealth(m, type);
                    var detail = ReadDetail(m);
                    components.Add(new ComponentRecord(target.HostId, type, name, state, detail, now));
                    rollup = MaxRollup(rollup, state);
                }
            };
            members(processors, ComponentTypes.Cpu);
            members(memory, ComponentTypes.Memory);

            if (thermal is { } th)
            {
                var temps = th.TryGetProperty("Temperatures", out var t) ? (JsonElement?)t : null;
                if (temps is { } tm)
                {
                    foreach (var temp in tm.EnumerateArray())
                    {
                        if (!temp.TryGetProperty("Name", out var nameProp)) continue;
                        var name = nameProp.GetString() ?? "temp";
                        var state = MapHealth(temp, ComponentTypes.Temp);
                        components.Add(new ComponentRecord(target.HostId, ComponentTypes.Temp, name, state,
                            ReadDetail(temp), now));
                        rollup = MaxRollup(rollup, state);
                        if (temp.TryGetProperty("ReadingCelsius", out var rc) && rc.ValueKind == JsonValueKind.Number)
                            metrics.Add(new MetricRecord(target.HostId, $"temperature:{name}", rc.GetDouble(), "C", now));
                    }
                }
                var fans = th.TryGetProperty("Fans", out var f) ? (JsonElement?)f : null;
                if (fans is { } fn)
                {
                    foreach (var fan in fn.EnumerateArray())
                    {
                        if (!fan.TryGetProperty("Name", out var nameProp)) continue;
                        var name = nameProp.GetString() ?? "fan";
                        components.Add(new ComponentRecord(target.HostId, ComponentTypes.Fan, name,
                            MapHealth(fan, ComponentTypes.Fan), ReadDetail(fan), now));
                        rollup = MaxRollup(rollup, MapHealth(fan, ComponentTypes.Fan));
                    }
                }
            }

            if (power is { } pw)
            {
                var supplies = pw.TryGetProperty("PowerSupplies", out var ps) ? (JsonElement?)ps : null;
                if (supplies is { } sp)
                {
                    foreach (var psu in sp.EnumerateArray())
                    {
                        if (!psu.TryGetProperty("Name", out var nameProp)) continue;
                        var name = nameProp.GetString() ?? "psu";
                        components.Add(new ComponentRecord(target.HostId, ComponentTypes.Psu, name,
                            MapHealth(psu, ComponentTypes.Psu), ReadDetail(psu), now));
                        rollup = MaxRollup(rollup, MapHealth(psu, ComponentTypes.Psu));
                    }
                }
                if (pw.TryGetProperty("PowerControl", out var pc))
                {
                    foreach (var ctrl in pc.EnumerateArray())
                    {
                        if (ctrl.TryGetProperty("PowerConsumedWatts", out var watts) && watts.ValueKind == JsonValueKind.Number)
                            metrics.Add(new MetricRecord(target.HostId, "power:consumed", watts.GetDouble(), "W", now));
                    }
                }
            }

            // Dell OEM: physical disks and controllers under the chassis.
            var oem = chassis is { } ch && ch.TryGetProperty("Oem", out var oemEl)
                && oemEl.TryGetProperty("Dell", out var dell) ? (JsonElement?)dell : null;
            if (oem is { } oe)
            {
                if (oe.TryGetProperty("DellPhysicalDisk", out var disks) && disks.TryGetProperty("Members", out var dm))
                    CollectDellOem(target, components, now, dm, ComponentTypes.Disk, ref rollup);
                if (oe.TryGetProperty("DellController", out var ctrls) && ctrls.TryGetProperty("Members", out var cm))
                    CollectDellOem(target, components, now, cm, ComponentTypes.Controller, ref rollup);
            }

            components.Add(new ComponentRecord(target.HostId, ComponentTypes.System, "System.Embedded.1",
                MapHealth(system.Value, ComponentTypes.System), null, now));
            rollup = MaxRollup(rollup, MapHealth(system.Value, ComponentTypes.System));

            return new HardwarePollResult(true, now, rollup, components, metrics, null);
        }
        catch (Exception ex)
        {
            log.LogWarning("Redfish poll failed for {host}: {error}", target.Name, ex.Message);
            return new HardwarePollResult(false, DateTimeOffset.UtcNow, "unknown", [], [], ex.Message);
        }
    }

    private void CollectDellOem(HardwarePollTarget target, List<ComponentRecord> components,
        DateTimeOffset now, JsonElement members, string type, ref string rollup)
    {
        foreach (var m in members.EnumerateArray())
        {
            var name = m.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : "unknown";
            var state = MapHealth(m, type);
            components.Add(new ComponentRecord(target.HostId, type, name, state, ReadDetail(m), now));
            rollup = MaxRollup(rollup, state);
        }
    }

    private static async Task<JsonElement?> GetJsonAsync(HttpClient client, Uri baseUri,
        string path, string auth, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static HealthState MapHealth(JsonElement el, string type)
    {
        if (el.TryGetProperty("Status", out var status) && status.TryGetProperty("Health", out var health)
            && health.ValueKind == JsonValueKind.String)
        {
            return health.GetString() switch
            {
                "OK" => HealthState.Ok,
                "Warning" => HealthState.Warning,
                "Critical" => HealthState.Critical,
                _ => HealthState.Unknown,
            };
        }
        if (el.TryGetProperty("Status", out var st) && st.TryGetProperty("State", out var state)
            && state.ValueKind == JsonValueKind.String)
        {
            return state.GetString() == "Enabled" ? HealthState.Ok : HealthState.Unknown;
        }
        return HealthState.Unknown;
    }

    private static string? ReadDetail(JsonElement el)
    {
        var parts = new List<string>();
        if (el.TryGetProperty("Status", out var st))
        {
            if (st.TryGetProperty("HealthRollup", out var hr) && hr.ValueKind == JsonValueKind.String)
                parts.Add($"HealthRollup={hr.GetString()}");
            if (st.TryGetProperty("Health", out var h) && h.ValueKind == JsonValueKind.String)
                parts.Add($"Health={h.GetString()}");
        }
        foreach (var key in new[] { "Model", "Manufacturer", "SerialNumber", "CapacityBytes", "FailurePredicted" })
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                parts.Add($"{key}={v}");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private static string MaxRollup(string current, HealthState newState)
    {
        var cur = HealthStates.FromWire(current);
        return HealthStates.ToWire(HealthStates.Max(cur, newState));
    }
}
