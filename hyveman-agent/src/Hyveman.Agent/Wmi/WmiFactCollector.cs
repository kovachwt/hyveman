using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Hyveman.Agent.Net;
using Hyveman.Agent.Options;
using Hyveman.Agent.Pipeline;
using Hyveman.Agent.Telemetry;

namespace Hyveman.Agent.Wmi;

/// <summary>
/// Serialized, timeout-bounded WMI scanner (AGENT.md §4.4, §7): one scan per
/// wmi.scan_interval_s; every query has an OperationTimeout; the previous scan
/// is cached and re-sent with stale=true when a scan times out; results go out
/// best-effort via TelemetrySender. A hung provider releases in seconds and
/// Hyper-V's own WMI usage is never starved (H2).
/// </summary>
public sealed class WmiFactCollector : BackgroundService
{
    private readonly OptionsSnapshot _snapshot;
    private readonly RuntimeMonitor _monitor;
    private readonly TelemetrySender _sender;
    private readonly ILogger<WmiFactCollector> _log;

    private List<VmFact>? _lastFacts;
    private int _consecutiveFailures;

    public WmiFactCollector(
        OptionsSnapshot snapshot,
        RuntimeMonitor monitor,
        TelemetrySender sender,
        ILogger<WmiFactCollector> log)
    {
        _snapshot = snapshot;
        _monitor = monitor;
        _sender = sender;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("WMI collector loop starting");
        // Stagger start so boot-time WMI pressure is not amplified.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(_snapshot.Active.Wmi.ScanIntervalS);
            try
            {
                await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "WMI scan failed");
                _monitor.AddWmiTimeouts(1);
                _monitor.SetDegraded("wmi_degraded");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        var opts = _snapshot.Active;
        var timeout = TimeSpan.FromSeconds(opts.Wmi.QueryTimeoutS);

        using var session = CimSession.Create(null, new CimSessionOptions { Timeout = timeout });

        List<VmFact> facts;
        try
        {
            facts = await Task.Run(() => QueryFacts(session, opts), ct).ConfigureAwait(false);
        }
        catch (CimException cim) when ((uint)cim.NativeErrorCode is 0x8004100E or 0x80041002 or 0x80041010 || IsNamespaceMissing(cim))
        {
            // 0x8004100E = WBEM_E_INVALID_NAMESPACE, 0x80041002 = NOT_FOUND,
            // 0x80041010 = WBEM_E_INVALID_CLASS: not a Hyper-V host (guest VM
            // installs, non-Hyper-V boxes). Retry on a slow cadence so a host
            // that gains Hyper-V later is picked up.
            _consecutiveFailures++;
            if (_consecutiveFailures == 1)
                _log.LogInformation("root\\virtualization\\v2 not available — not a Hyper-V host? (WMI facts disabled, will re-check periodically)");
            return;
        }
        catch (CimException cim) when ((uint)cim.NativeErrorCode == 0x80041002)
        {
            // WBEM_E_NOT_FOUND — no Msvm_ComputerSystem instances (no VMs).
            facts = new List<VmFact>();
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _monitor.AddWmiTimeouts(1);
            _monitor.SetDegraded("wmi_degraded");
            _log.LogWarning(ex, "WMI scan failed (timeout or provider hang); re-sending prior facts stale=true");

            if (_lastFacts is not null)
                await SendFactsAsync(_lastFacts, stale: true, ct).ConfigureAwait(false);
            return;
        }

        _consecutiveFailures = 0;
        _lastFacts = facts;
        _monitor.ClearDegraded("wmi_degraded");
        await SendFactsAsync(facts, stale: false, ct).ConfigureAwait(false);
    }

    private static bool IsNamespaceMissing(CimException cim)
        => cim.Message.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
           (uint)cim.NativeErrorCode is 0x8004100E or 0x80070002;

    /// <summary>One serialized scan: VM list + per-VM summary (AGENT §7).</summary>
    private static List<VmFact> QueryFacts(CimSession session, AgentOptions opts)
    {
        var ns = HyperVQueries.Namespace;
        var queries = 0;
        var maxQueries = opts.Wmi.MaxQueriesPerScan;

        // 1. VM list (Msvm_ComputerSystem) — also the SettingData refs source.
        //    NOTE: WQL must go through QueryInstances — EnumerateInstances treats
        //    its second argument as a class NAME, not a query.
        var vms = new List<CimInstance>();
        foreach (var instance in session.QueryInstances(ns, "WQL", HyperVQueries.VmListWql))
        {
            if (++queries > maxQueries) break;
            vms.Add(instance);
        }

        // 2. Per-VM summary via GetSummaryInformation (single method call).
        var facts = new List<VmFact>();
        var service = session.EnumerateInstances(ns, HyperVQueries.ServiceClass).FirstOrDefault();
        if (service is null || ++queries > maxQueries)
            return facts;

        var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("RequestedInformation", HyperVQueries.SummaryRequested, CimType.UInt32Array, CimFlags.None),
            CimMethodParameter.Create("SettingData", vms.ToArray(), CimType.ReferenceArray, CimFlags.None)
        };

        var result = session.InvokeMethod(ns, service, "GetSummaryInformation", inParams);
        if (result is null)
            return facts;

        var outVal = result.OutParameters?["SummaryInformation"]?.Value;
        if (outVal is CimInstance[] summaries)
        {
            foreach (var s in summaries)
            {
                var fact = HyperVQueries.ToVmFact(s);
                if (fact is not null)
                    facts.Add(fact);
            }
        }
        else if (outVal is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is CimInstance s)
                {
                    var fact = HyperVQueries.ToVmFact(s);
                    if (fact is not null)
                        facts.Add(fact);
                }
            }
        }

        return facts;
    }

    private async Task SendFactsAsync(List<VmFact> facts, bool stale, CancellationToken ct)
    {
        var item = new FactsItem
        {
            CollectedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Stale = stale,
            Vms = facts.Select(f => new VmFactWire
            {
                Name = f.Name,
                State = f.State,
                HeartbeatOk = f.HeartbeatOk,
                CpuPct = f.CpuPct,
                MemMb = f.MemMb,
                LastSeen = f.LastSeenUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            }).ToList()
        };

        _log.LogDebug("Sending facts: {count} VMs (stale={stale})", facts.Count, stale);
        await _sender.SendAsync(item, _snapshot.Active.SourceId, ct).ConfigureAwait(false);
    }
}
