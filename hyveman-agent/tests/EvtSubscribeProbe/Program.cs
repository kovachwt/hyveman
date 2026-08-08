using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

var session = CimSession.Create(null, new CimSessionOptions { Timeout = TimeSpan.FromSeconds(10) });
try
{
    var vms = session.QueryInstances(@"root\virtualization\v2", "WQL", "SELECT * FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'").ToList();
    Console.WriteLine($"VMs: {vms.Count}");
    foreach (var vm in vms)
        Console.WriteLine($"  {vm.CimInstanceProperties["Name"]?.Value} enabled={vm.CimInstanceProperties["EnabledState"]?.Value} caption={vm.CimInstanceProperties["Caption"]?.Value}");

    var svc = session.EnumerateInstances(@"root\virtualization\v2", "Msvm_VirtualSystemManagementService").FirstOrDefault();
    Console.WriteLine($"svc: {svc?.CimInstanceProperties["Name"]?.Value}");

    if (svc is not null)
    {
        var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("RequestedInformation", new uint[] { 0, 1, 100, 101, 103, 104, 105, 109, 112 }, CimType.UInt32Array, CimFlags.None),
            CimMethodParameter.Create("SettingData", vms.ToArray(), CimType.ReferenceArray, CimFlags.None)
        };
        var result = session.InvokeMethod(@"root\virtualization\v2", svc, "GetSummaryInformation", inParams);
        var outVal = result.OutParameters?["SummaryInformation"]?.Value;
        Console.WriteLine($"summary type: {outVal?.GetType().Name}");
        if (outVal is CimInstance[] arr)
        {
            foreach (var s in arr)
            {
                Console.WriteLine($"  {s.CimInstanceProperties["Name"]?.Value} state={s.CimInstanceProperties["EnabledState"]?.Value} cpu={s.CimInstanceProperties["ProcessorLoad"]?.Value} mem={s.CimInstanceProperties["MemoryUsage"]?.Value} hb={s.CimInstanceProperties["Heartbeat"]?.Value}");
            }
        }
        else if (outVal is System.Collections.IEnumerable en)
        {
            foreach (var item in en) Console.WriteLine($"  item: {item?.GetType().Name}");
        }
        else Console.WriteLine($"  (null)");
    }
}
catch (CimException cim)
{
    Console.WriteLine($"CimException: '{cim.Message}' 0x{(uint)cim.NativeErrorCode:X8}");
}
Console.WriteLine("done");
return 0;
