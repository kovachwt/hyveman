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

    /// <summary>One serialized scan: VM list + per-VM summary + replication
    /// relationships (AGENT §7).</summary>
    private List<VmFact> QueryFacts(CimSession session, AgentOptions opts)
    {
        var ns = HyperVQueries.Namespace;
        // Budget counts WMI *operations* (provider calls), never result
        // instances: one QueryInstances returns any number of VMs. Counting
        // instances here meant hosts with >= max_queries_per_scan VMs silently
        // reported zero VMs (see QueryBudget).
        var budget = new QueryBudget(opts.Wmi.MaxQueriesPerScan);

        // 1. VM list (Msvm_ComputerSystem) — also the SettingData refs source.
        //    NOTE: WQL must go through QueryInstances — EnumerateInstances treats
        //    its second argument as a class NAME, not a query.
        var vms = new List<CimInstance>();
        if (budget.TrySpend())
        {
            foreach (var instance in session.QueryInstances(ns, "WQL", HyperVQueries.VmListWql))
                vms.Add(instance);
        }

        // 2. Per-VM summary via GetSummaryInformation (single method call).
        var facts = new List<VmFact>();
        var summaries = new List<CimInstance>();
        if (!budget.TrySpend())
            return facts;
        var service = session.EnumerateInstances(ns, HyperVQueries.ServiceClass).FirstOrDefault();
        if (service is null)
            return facts;

        if (!budget.TrySpend())
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
        if (outVal is CimInstance[] arr)
            summaries.AddRange(arr);
        else if (outVal is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                if (item is CimInstance s)
                    summaries.Add(s);
        }

        // 3. Replication relationships (AGENT.md §7): one extra operation in
        // the budget. Joined to the summaries by VM GUID (parsed from
        // InstanceID) with an ElementName fallback — the relationship class's
        // key properties (SystemName/Name) come back EMPTY from the provider
        // (verified on Server 2019), so ElementName/InstanceID are the real
        // join surface. A host where this class is unavailable (or the query
        // fails) must not fail the scan: replication facts are best-effort,
        // the rest of the scan is unaffected. The heartbeat counter reports
        // the found count (0 = genuinely no replication configured, -1 =
        // query unavailable) so the backend can tell the two apart.
        // OrdinalIgnoreCase: casing is not guaranteed to match between the
        // summary and relationship instances.
        var replicationByGuid = new Dictionary<string, ReplicationFact>(StringComparer.OrdinalIgnoreCase);
        var replicationByName = new Dictionary<string, ReplicationFact>(StringComparer.OrdinalIgnoreCase);
        if (budget.TrySpend())
        {
            try
            {
                foreach (var rel in session.QueryInstances(ns, "WQL", HyperVQueries.ReplicationRelationshipWql))
                {
                    var repl = HyperVQueries.ToReplicationFact(rel);
                    if (repl is null)
                        continue;
                    if (repl.VmGuid is not null)
                        replicationByGuid[repl.VmGuid] = repl;
                    if (repl.VmElementName is not null)
                        replicationByName[repl.VmElementName] = repl;
                }
                _monitor.SetReplicationRelationships(replicationByGuid.Count > 0 || replicationByName.Count > 0
                    ? Math.Max(replicationByGuid.Count, replicationByName.Count)
                    : 0);
            }
            catch (CimException ex) when ((uint)ex.NativeErrorCode is 0x8004100E or 0x80041002 or 0x80041010)
            {
                // Class/namespace missing on this host (pre-2012 R2 Hyper-V,
                // exotic providers): replication facts simply absent.
                _monitor.SetReplicationRelationships(-1);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Replication relationship scan failed; replication facts omitted for this scan");
                _monitor.SetReplicationRelationships(-1);
            }
        }

        foreach (var s in summaries)
        {
            var fact = HyperVQueries.ToVmFact(s, replicationByGuid, replicationByName);
            if (fact is not null)
                facts.Add(fact);
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
                ReplicationState = f.ReplicationState,
                ReplicationHealth = f.ReplicationHealth,
                ReplicationLastApplyTime = f.LastApplyTimeUtc is { } lat
                    ? lat.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                    : null,
                LastSeen = f.LastSeenUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            }).ToList()
        };

        _log.LogDebug("Sending facts: {count} VMs (stale={stale})", facts.Count, stale);
        await _sender.SendAsync(item, _snapshot.Active.SourceId, ct).ConfigureAwait(false);
    }
}
