using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hyveman.Agent.Options;
using Hyveman.Agent.Wevtapi;

namespace Hyveman.Agent.Net;

/// <summary>
/// Maps rendered Windows events onto the generic wire envelope (AGENT.md §9,
/// App. A; PROTOCOL §6/§17). Pure logic — unit tested.
/// </summary>
public sealed class EnvelopeBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly LimitsOptions _limits;

    public EnvelopeBuilder(LimitsOptions limits) => _limits = limits;

    public const string TruncationMarkerPrefix = "…hyveman-truncated:";

    /// <summary>record_id per PROTOCOL §11.1: bare, or "e&lt;epoch&gt;:&lt;id&gt;" after a channel reset.</summary>
    public static string RecordIdFor(ulong recordId, int epoch)
        => epoch > 0 ? $"e{epoch}:{recordId.ToString(CultureInfo.InvariantCulture)}" : recordId.ToString(CultureInfo.InvariantCulture);

    public LogItem BuildLogItem(EvtLogEvent ev)
    {
        return new LogItem
        {
            RecordId = RecordIdFor(ev.RecordId, ev.Epoch),
            DedupScope = ev.DedupScope.Length > 0 ? ev.DedupScope : ev.Channel,
            Time = ev.TimeCreatedUtc != DateTime.MinValue
                ? ev.TimeCreatedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                : DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            Severity = ev.Level > 0 ? ev.Level : null, // omit when Level unspecified (0) — PROTOCOL §10
            Facility = ev.ProviderName,                 // null when provider absent — never a literal "unknown"
            Message = ev.Message ?? FallbackMessage(ev),
            Fields = new LogFields
            {
                Channel = ev.Channel,
                EventId = ev.EventId,
                Task = ev.Task,
                Opcode = ev.Opcode,
                Keywords = "0x" + ev.Keywords.ToString("x", CultureInfo.InvariantCulture),
                ProviderGuid = ev.ProviderGuid,
                Computer = ev.Computer,
                ActivityId = ev.ActivityId?.ToString(),
                ProcessId = ev.ProcessId,
                ThreadId = ev.ThreadId,
                EventData = ev.EventData
            },
            Raw = TruncateRaw(ev.RawXml)
        };
    }

    private static string FallbackMessage(EvtLogEvent ev)
        => $"Event {ev.EventId} from {ev.ProviderName ?? "unknown provider"}";

    /// <summary>
    /// raw capped at max_raw_bytes (default 8 KiB); over-cap → truncated with
    /// marker "…hyveman-truncated:&lt;n&gt;" (AGENT §9.3, PROTOCOL §12).
    /// </summary>
    public string? TruncateRaw(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        var budget = _limits.MaxRawBytes;
        var marker = TruncationMarkerPrefix + budget.ToString(CultureInfo.InvariantCulture);
        var markerBytes = Encoding.UTF8.GetByteCount(marker);

        if (Encoding.UTF8.GetByteCount(raw) <= budget)
            return raw;

        // Keep whole UTF-16 chars while fitting (budget - markerBytes).
        var cut = budget - markerBytes;
        var sb = new StringBuilder();
        var used = 0;
        foreach (var ch in raw)
        {
            var charBytes = Encoding.UTF8.GetByteCount(ch.ToString());
            if (used + charBytes > cut)
                break;
            sb.Append(ch);
            used += charBytes;
        }
        return sb.ToString() + marker;
    }

    public byte[] Serialize(LogBatchEnvelope batch) => JsonSerializer.SerializeToUtf8Bytes(batch, JsonOpts);

    /// <summary>
    /// Builds batch chunks for a list of events, splitting when the body would
    /// exceed max_batch_bytes (AGENT §9.3). Each chunk pairs its JSON bytes
    /// with the events it covers (1:1 order) so the caller can advance
    /// bookmarks only for events that were durably spooled.
    /// </summary>
    public List<(byte[] Json, List<EvtLogEvent> Events)> BuildBatches(IReadOnlyList<EvtLogEvent> events, string? sourceId)
    {
        var items = events.Select(ev => BuildLogItem(ev)).ToList();
        var batch = new LogBatchEnvelope { Source = sourceId, Items = items };
        return ChunkToSize(batch, events.ToList());
    }

    private List<(byte[] Json, List<EvtLogEvent> Events)> ChunkToSize(LogBatchEnvelope batch, List<EvtLogEvent> events)
    {
        var json = Serialize(batch);
        if (json.Length <= _limits.MaxBatchBytes)
            return new List<(byte[], List<EvtLogEvent>)> { (json, events) };

        if (batch.Items.Count <= 1)
        {
            // A single over-large item is still sent (raw already truncated);
            // this is a hard floor — send it and let the server size-check.
            return new List<(byte[], List<EvtLogEvent>)> { (json, events) };
        }

        var mid = batch.Items.Count / 2;
        var left = new LogBatchEnvelope { Source = batch.Source, Items = batch.Items.Take(mid).ToList() };
        var right = new LogBatchEnvelope { Source = batch.Source, Items = batch.Items.Skip(mid).ToList() };
        var result = new List<(byte[], List<EvtLogEvent>)>();
        result.AddRange(ChunkToSize(left, events.Take(mid).ToList()));
        result.AddRange(ChunkToSize(right, events.Skip(mid).ToList()));
        return result;
    }

    /// <summary>Re-chunks a batch read back from a spool file into halves (used when the server asks to split).</summary>
    public List<(byte[] Json, int Items)> SplitInHalf(byte[] batchJson)
    {
        var batch = JsonSerializer.Deserialize<LogBatchEnvelope>(batchJson, JsonOpts);
        if (batch is null || batch.Items.Count <= 1)
            return new List<(byte[], int)> { (batchJson, batch?.Items.Count ?? 1) };

        var mid = batch.Items.Count / 2;
        var left = new LogBatchEnvelope { Source = batch.Source, Items = batch.Items.Take(mid).ToList() };
        var right = new LogBatchEnvelope { Source = batch.Source, Items = batch.Items.Skip(mid).ToList() };
        var result = new List<(byte[], int)>();
        foreach (var part in new[] { left, right })
        {
            var json = Serialize(part);
            if (json.Length > _limits.MaxBatchBytes)
            {
                // Recursively split any part still over the cap.
                result.AddRange(SplitInHalf(json));
            }
            else
            {
                result.Add((json, part.Items.Count));
            }
        }
        return result;
    }
}
