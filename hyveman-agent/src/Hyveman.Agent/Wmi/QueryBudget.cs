namespace Hyveman.Agent.Wmi;

/// <summary>
/// Per-scan budget for WMI *operations* (AGENT.md §4.4 rule 2: bound the number
/// of provider calls per scan so one scan can never become a provider storm).
/// The budget counts operations — a single QueryInstances returns any number of
/// VM instances, so result counts must never spend it. (Regression: the scan
/// previously counted VM instances against this budget, so any host with
/// &gt;= max_queries_per_scan VMs silently reported zero VMs.)
/// </summary>
public sealed class QueryBudget
{
    private readonly int _max;
    private int _spent;

    public QueryBudget(int max)
    {
        _max = Math.Max(max, 1);
    }

    /// <summary>True if the budget has room for one more operation.</summary>
    public bool TrySpend()
    {
        if (_spent >= _max)
            return false;
        _spent++;
        return true;
    }

    public int Remaining => _max - _spent;
}
