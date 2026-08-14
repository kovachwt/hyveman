using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
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
/// <remarks>Certificate policy (API.md §9.1): "strict" validates against the
/// OS trust store via the shared "redfish" named client; "trust-on-first-use"
/// accepts and pins the first certificate a host presents, refusing that host
/// if the certificate later changes (pins live in IIdracCertStore).</remarks>
/// <remarks>Link safety (API.md 12, SECURITY-REVIEW-2026-08-14 M1): every
/// `@odata.id` followed by the poller is resolved against the host's base
/// URI and must stay on that URI's origin (same scheme + authority). A
/// compromised or MITM'd iDRAC that returns an absolute attacker URL - or a
/// protocol-relative `//host/...` link, or an http scheme downgrade - must
/// never receive the Basic credentials or turn the poller into an SSRF
/// primitive; the offending member is skipped with a warning. Same-origin
/// absolute links (rare but spec-legal) are followed.</remarks>
public sealed class DellRedfishProvider(
    IHttpClientFactory http,
    string idracCertPolicy,
    IIdracCertStore certs,
    ILogger<DellRedfishProvider> log) : IHardwareProvider
{
    public async Task<HardwarePollResult> PollAsync(HardwarePollTarget target, CancellationToken ct)
    {
        // Certificate pin state for this poll, populated by the TLS callback
        // when trust-on-first-use accepts an untrusted certificate.
        string? acceptedFingerprint = null;
        byte[]? acceptedDer = null;
        var pinnedFingerprint = idracCertPolicy == IdracCertPolicies.TrustOnFirstUse
            ? await certs.GetFingerprintAsync(target.HostId, ct)
            : null;
        try
        {
            var client = BuildClient(pinnedFingerprint,
                (cert, errors) =>
                {
                    if (errors == SslPolicyErrors.None) return true;
                    if (cert is null) return false;
                    var fp = IdracCertPolicies.FingerprintOf(cert.RawData);
                    if (pinnedFingerprint is not null) return fp == pinnedFingerprint;
                    acceptedFingerprint = fp;
                    acceptedDer = cert.RawData;
                    return true;
                });
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
            var storage = await GetJsonAsync(client, baseUri, "redfish/v1/Systems/System.Embedded.1/Storage", auth, ct);
            var thermal = await GetJsonAsync(client, baseUri, "redfish/v1/Chassis/System.Embedded.1/Thermal", auth, ct);
            var power = await GetJsonAsync(client, baseUri, "redfish/v1/Chassis/System.Embedded.1/Power", auth, ct);

            // Redfish collections (Processors/Memory/Storage) return bare link
            // objects in Members — member resources are only inlined when the
            // request asks for ?$expand, which iDRAC does not honor here. Each
            // link is followed so CPUs, DIMMs, storage controllers and
            // physical disks actually reach the component table (D4).
            rollup = await CollectLinkedMembersAsync(client, baseUri, auth, ct, processors, ComponentTypes.Cpu,
                target.HostId, components, now, rollup);
            rollup = await CollectLinkedMembersAsync(client, baseUri, auth, ct, memory, ComponentTypes.Memory,
                target.HostId, components, now, rollup);
            rollup = await CollectStorageAsync(client, baseUri, auth, ct, storage,
                target.HostId, components, now, rollup);

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
        finally
        {
            // Remember the certificate accepted on first use (API.md §9.1).
            // Persisted even when the poll itself failed later, so the next
            // poll uses the pin instead of re-accepting an unknown cert.
            if (acceptedFingerprint is not null && acceptedDer is not null)
            {
                try
                {
                    await certs.SetAsync(target.HostId, acceptedDer, acceptedFingerprint,
                        DateTimeOffset.UtcNow, CancellationToken.None);
                    log.LogInformation("Pinned iDRAC certificate for {host} ({fingerprint})",
                        target.Name, acceptedFingerprint);
                }
                catch (Exception ex)
                {
                    log.LogWarning("Failed to persist iDRAC certificate pin for {host}: {error}",
                        target.Name, ex.Message);
                }
            }
        }
    }

    private HttpClient BuildClient(string? pinnedFingerprint,
        Func<X509Certificate2?, SslPolicyErrors, bool> validate)
    {
        if (idracCertPolicy != IdracCertPolicies.TrustOnFirstUse)
            return http.CreateClient("redfish");

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, // the Redfish client must not follow arbitrary redirects (API.md §12)
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) => validate(cert, errors),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Normalizes a Redfish collection into components. Members are
    /// bare link objects ({"@odata.id": ...}) on real iDRACs; each link is
    /// fetched and normalized. Inlined members (Name present) are used as-is.
    /// (D4: collections were previously treated as inline-only, so every
    /// processor and DIMM member was silently skipped.)</summary>
    private async Task<string> CollectLinkedMembersAsync(HttpClient client, Uri baseUri, string auth, CancellationToken ct,
        JsonElement? collection, string type, string hostId, List<ComponentRecord> components,
        DateTimeOffset now, string rollup)
    {
        if (collection is not { } c || !c.TryGetProperty("Members", out var ms)) return rollup;
        foreach (var member in ms.EnumerateArray())
        {
            var el = member;
            if (!el.TryGetProperty("Name", out var nameProp))
            {
                if (!el.TryGetProperty("@odata.id", out var link) || link.ValueKind != JsonValueKind.String) continue;
                var resource = await GetJsonAsync(client, baseUri, link.GetString()!, auth, ct);
                if (resource is not { } r || !r.TryGetProperty("Name", out nameProp)) continue;
                el = r;
            }
            var name = nameProp.GetString() ?? "unknown";
            var state = MapHealth(el, type);
            components.Add(new ComponentRecord(hostId, type, name, state, ReadDetail(el), now));
            rollup = MaxRollup(rollup, state);
        }
        return rollup;
    }

    /// <summary>Dell exposes storage controllers under
    /// /Systems/&lt;system&gt;/Storage and physical disks under each
    /// controller's Drives array. The previous OEM path —
    /// Oem.Dell.DellPhysicalDisk/DellController on the Chassis resource — does
    /// not exist on real iDRACs (verified against a fleet iDRAC9: Chassis
    /// Oem.Dell carries only DellChassis), so disks and controllers never
    /// reached the component table (D4).</summary>
    private async Task<string> CollectStorageAsync(HttpClient client, Uri baseUri, string auth, CancellationToken ct,
        JsonElement? storage, string hostId, List<ComponentRecord> components, DateTimeOffset now, string rollup)
    {
        if (storage is not { } s || !s.TryGetProperty("Members", out var controllers)) return rollup;
        foreach (var controllerLink in controllers.EnumerateArray())
        {
            if (!controllerLink.TryGetProperty("@odata.id", out var cLink) || cLink.ValueKind != JsonValueKind.String) continue;
            var controller = await GetJsonAsync(client, baseUri, cLink.GetString()!, auth, ct);
            if (controller is not { } ctl) continue;

            if (ctl.TryGetProperty("Name", out var cName) && cName.ValueKind == JsonValueKind.String)
            {
                var name = cName.GetString() ?? "unknown";
                var state = MapHealth(ctl, ComponentTypes.Controller);
                components.Add(new ComponentRecord(hostId, ComponentTypes.Controller, name, state, ReadDetail(ctl), now));
                rollup = MaxRollup(rollup, state);
            }

            if (!ctl.TryGetProperty("Drives", out var drives)) continue;
            foreach (var driveLink in drives.EnumerateArray())
            {
                if (!driveLink.TryGetProperty("@odata.id", out var dLink) || dLink.ValueKind != JsonValueKind.String) continue;
                var drive = await GetJsonAsync(client, baseUri, dLink.GetString()!, auth, ct);
                if (drive is not { } d || !d.TryGetProperty("Name", out var dName)
                    || dName.ValueKind != JsonValueKind.String) continue;
                var name = dName.GetString() ?? "unknown";
                var state = MapHealth(d, ComponentTypes.Disk);
                // Predictive disk failure (SMART alert) is the motivating case
                // for the disk component (DESIGN §4.4); some firmware keeps
                // Status=OK while FailurePredicted is set, so escalate rather
                // than trust Status alone.
                if (d.TryGetProperty("FailurePredicted", out var fp) && fp.ValueKind == JsonValueKind.True)
                    state = HealthStates.Max(state, HealthState.Warning);
                components.Add(new ComponentRecord(hostId, ComponentTypes.Disk, name, state, ReadDetail(d), now));
                rollup = MaxRollup(rollup, state);
            }
        }
        return rollup;
    }

    /// <summary>Fetches one Redfish resource with Basic auth. The requested
    /// path - a fixed literal for the top-level resources, a device-supplied
    /// `@odata.id` for collection members - must resolve onto the base URI's
    /// origin; off-origin targets are refused before any request is sent so
    /// the credentials can never leave the iDRAC's own host
    /// (SECURITY-REVIEW-2026-08-14 M1).</summary>
    private async Task<JsonElement?> GetJsonAsync(HttpClient client, Uri baseUri,
        string path, string auth, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var uri = new Uri(baseUri, path);
            // Uri.Authority excludes userinfo, so compare host/port explicitly
            // and forbid any userinfo on the target: a link like
            // https://root:calvin@idrac.example/... otherwise compares
            // "same-origin" while smuggling its own authority.
            if (uri.Scheme != baseUri.Scheme || uri.Host != baseUri.Host || uri.Port != baseUri.Port
                || uri.UserInfo.Length > 0)
            {
                log.LogWarning(
                    "Redfish link '{Link}' (truncated) on {Host} resolves off-origin to {Scheme}://{Authority}; refusing to follow (M1)",
                    path.Length <= 300 ? path : path[..300], baseUri.Host, uri.Scheme, uri.Authority);
                return null;
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
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
