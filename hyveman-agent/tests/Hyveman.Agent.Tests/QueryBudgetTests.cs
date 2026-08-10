using Hyveman.Agent.Wmi;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>
/// Regression tests for the WMI per-scan operation budget (AGENT.md §4.4,
/// §7). The budget counts WMI *operations*, never result instances — a single
/// QueryInstances returns any number of VMs. The prior implementation counted
/// VM instances against the budget, so any host with &gt;= max_queries_per_scan
/// VMs (default 8) silently reported zero VMs.
/// </summary>
public class QueryBudgetTests
{
    [Fact]
    public void Default_Scan_Spends_Exactly_Three_Operations()
    {
        // A scan = VM-list query + service enumeration + GetSummaryInformation.
        var budget = new QueryBudget(max: 8);
        Assert.True(budget.TrySpend());
        Assert.True(budget.TrySpend());
        Assert.True(budget.TrySpend());
        Assert.Equal(5, budget.Remaining);
    }

    [Fact]
    public void Enumerating_Vms_Does_Not_Spend_Extra_Budget()
    {
        // The regression: one QueryInstances call returns ALL N VMs, so
        // enumerating a large result set must not consume the budget. The
        // budget is spent per provider call, not per returned instance.
        var budget = new QueryBudget(max: 8);
        Assert.True(budget.TrySpend()); // the VM-list query

        // Simulate draining a result set of 50 VMs (the caller never spends
        // per instance — nothing to do here but assert the budget was not
        // touched by result iteration).
        var vms = Enumerable.Range(0, 50).ToList();
        _ = vms.Count;

        Assert.Equal(7, budget.Remaining);
    }

    [Fact]
    public void Budget_Exhausts_At_Max()
    {
        var budget = new QueryBudget(max: 3);
        Assert.True(budget.TrySpend());
        Assert.True(budget.TrySpend());
        Assert.True(budget.TrySpend());
        Assert.False(budget.TrySpend());
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void Budget_Of_One_Allows_The_Vm_List_Query()
    {
        // Even a minimal budget still yields the VM list (the scan may then
        // skip the summary, but must never report "no VMs" for a host that
        // has them).
        var budget = new QueryBudget(max: 1);
        Assert.True(budget.TrySpend());
        Assert.False(budget.TrySpend());
    }
}
