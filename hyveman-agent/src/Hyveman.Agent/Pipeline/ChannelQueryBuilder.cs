using System.Globalization;
using Hyveman.Agent.Options;
using Hyveman.Agent.Wevtapi;

namespace Hyveman.Agent.Pipeline;

/// <summary>
/// Pure query/filter logic (AGENT.md §6.2/§6.4) — unit tested.
/// </summary>
public static class ChannelQueryBuilder
{
    /// <summary>
    /// Builds the XPath that pushes level + ID filtering into the Event Log
    /// API (cheapest path): *[System[(Level&lt;=N) and (EventID=X or ...)]].
    /// EventData-field predicates are deliberately NOT expressed in XPath —
    /// the 4624 LogonType filter is applied in-process (see <see cref="SecurityFilter"/>).
    /// </summary>
    public static string Build(ChannelOptions config, SecurityLogOptions security, string actualChannel)
    {
        var isSecurity = string.Equals(actualChannel, "Security", StringComparison.OrdinalIgnoreCase);

        var preds = new List<string>();

        if (!string.IsNullOrEmpty(config.Provider))
            preds.Add($"Provider[@Name='{config.Provider}']");

        if (isSecurity && security.Enabled)
        {
            var ids = security.IncludeIds;
            preds.Add("(" + string.Join(" or ", ids.Select(id => $"EventID={id.ToString(CultureInfo.InvariantCulture)}")) + ")");
        }
        else
        {
            if (config.Level is { } lvl)
                preds.Add($"Level<={(int)lvl}");
            if (config.IncludeIds is { Count: > 0 } inc)
                preds.Add("(" + string.Join(" or ", inc.Select(id => $"EventID={id.ToString(CultureInfo.InvariantCulture)}")) + ")");
            if (config.ExcludeIds is { Count: > 0 } exc)
                preds.Add("(" + string.Join(" and ", exc.Select(id => $"EventID!={id.ToString(CultureInfo.InvariantCulture)}")) + ")");
        }

        return preds.Count == 0 ? "*" : "*[System[" + string.Join(" and ", preds) + "]]";
    }
}

/// <summary>
/// Curated Security post-filters (AGENT.md §6.4): keep only 4624 with
/// LogonType ∈ {2,10}; 4625/4740 pass through.
/// </summary>
public static class SecurityFilter
{
    public static bool ShouldKeep(EvtLogEvent ev, SecurityLogOptions security)
    {
        if (!string.Equals(ev.Channel, "Security", StringComparison.OrdinalIgnoreCase) || !security.Enabled)
            return true;

        if (ev.EventId != 4624)
            return true; // 4625 / 4740 pass through

        var allowed = security.LogonTypesFor4624;
        if (ev.EventData is null || !ev.EventData.TryGetValue("LogonType", out var v))
            return false; // malformed 4624 — drop (conservative)

        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var logonType))
            return false;

        return allowed.Contains(logonType);
    }
}
