using System.Text;
using System.Text.Json;

namespace Hyveman.Agent.Options;

/// <summary>
/// Snake_case naming policy for agent.json (the documented format uses
/// snake_case keys: min_free_bytes, include_ids, scan_interval_s, ...).
/// .NET 8 has no built-in SnakeCaseLower (added in .NET 9), so this is a
/// minimal implementation. DataDir → data_dir; BatchMaxAgeMs → batch_max_age_ms.
/// </summary>
public sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public static readonly SnakeCaseNamingPolicy Instance = new();

    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                // Insert '_' between words: before an upper char that follows a
                // lower char, or starts a new word after an acronym (e.g. XmlUrl → xml_url).
                if (i > 0 && (char.IsLower(name[i - 1]) ||
                              (i + 1 < name.Length && char.IsLower(name[i + 1]) && !char.IsUpper(name[i - 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
