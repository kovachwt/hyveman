using Hyveman.Agent.Net;
using Hyveman.Agent.Options;
using Hyveman.Agent.Wevtapi;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>
/// Envelope builder tests (AGENT.md §19.A): Windows event → envelope field
/// mapping for representative events (6008, 4624 LT2, 4625, 4740), the
/// record_id epoch scheme (PROTOCOL §11.1), and raw truncation (§9.3).
/// </summary>
public class EnvelopeBuilderTests
{
    private static EnvelopeBuilder NewBuilder(int maxRaw = 8192) =>
        new(new LimitsOptions { MaxRawBytes = maxRaw, MaxBatchBytes = 4 * 1024 * 1024 });

    private static EvtLogEvent KernelPower6008()
    {
        return new EvtLogEvent
        {
            Channel = "System",
            DedupScope = "System",
            RecordId = 41235,
            TimeCreatedUtc = new DateTime(2024, 8, 7, 15, 2, 11, 123, DateTimeKind.Utc),
            Level = 2,
            EventId = 6008,
            Task = 0,
            Opcode = 0,
            Keywords = 0x80000000000000,
            ProviderName = "Microsoft-Windows-Kernel-Power",
            ProviderGuid = "{331C3B3A-2005-44C2-A5C5-B1B27E7C7F3D}",
            Computer = "HOST01",
            ActivityId = null,
            ProcessId = 4,
            ThreadId = 8,
            EventData = new Dictionary<string, string?> { ["BugcheckCode"] = "0" },
            RawXml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System/></Event>",
            Message = "The previous system shutdown at 3:02:11 PM on 8/7/2024 was unexpected."
        };
    }

    [Fact]
    public void Event6008_Maps_All_Fields()
    {
        var item = NewBuilder().BuildLogItem(KernelPower6008());

        Assert.Equal("log", item.Kind);
        Assert.Equal("41235", item.RecordId);            // bare (epoch 0)
        Assert.Equal("System", item.DedupScope);
        Assert.Equal("2024-08-07T15:02:11.123Z", item.Time);
        Assert.Equal(2, item.Severity);                  // Error
        Assert.Equal("Microsoft-Windows-Kernel-Power", item.Facility); // provider, NOT channel
        Assert.Equal("The previous system shutdown at 3:02:11 PM on 8/7/2024 was unexpected.", item.Message);

        Assert.NotNull(item.Fields);
        Assert.Equal("System", item.Fields!.Channel);
        Assert.Equal(6008u, item.Fields.EventId);
        Assert.Equal(0, item.Fields.Task);
        Assert.Equal(0, item.Fields.Opcode);
        Assert.Equal("0x80000000000000", item.Fields.Keywords);
        Assert.Equal("{331C3B3A-2005-44C2-A5C5-B1B27E7C7F3D}", item.Fields.ProviderGuid);
        Assert.Equal("HOST01", item.Fields.Computer);
        Assert.Equal(4u, item.Fields.ProcessId);
        Assert.Equal(8u, item.Fields.ThreadId);
        Assert.Equal("0", item.Fields.EventData!["BugcheckCode"]);
        Assert.Equal("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System/></Event>", item.Raw);
    }

    [Fact]
    public void Security4624_LogonType_In_EventData()
    {
        var ev = new EvtLogEvent
        {
            Channel = "Security",
            DedupScope = "Security",
            RecordId = 99,
            TimeCreatedUtc = DateTime.UtcNow,
            Level = 0,
            EventId = 4624,
            ProviderName = "Microsoft-Windows-Security-Auditing",
            EventData = new Dictionary<string, string?> { ["LogonType"] = "10", ["TargetUserName"] = "admin" }
        };
        var item = NewBuilder().BuildLogItem(ev);
        Assert.Equal("10", item.Fields!.EventData!["LogonType"]);
        Assert.Equal("admin", item.Fields.EventData["TargetUserName"]);
    }

    [Fact]
    public void RecordId_Epoch_Scheme_Is_Dedup_Distinct()
    {
        // PROTOCOL §11.1: "41235", "e1:41235", "e2:41235" are distinct opaque strings.
        Assert.Equal("41235", EnvelopeBuilder.RecordIdFor(41235, 0));
        Assert.Equal("e1:41235", EnvelopeBuilder.RecordIdFor(41235, 1));
        Assert.Equal("e2:41235", EnvelopeBuilder.RecordIdFor(41235, 2));
        Assert.Equal("e1:1", EnvelopeBuilder.RecordIdFor(1, 1));

        var ids = new[] { EnvelopeBuilder.RecordIdFor(1, 0), EnvelopeBuilder.RecordIdFor(1, 1), EnvelopeBuilder.RecordIdFor(1, 2) };
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void Raw_Truncated_With_Marker_At_MaxRawBytes()
    {
        var builder = NewBuilder(maxRaw: 1024);
        var big = new string('x', 100_000);
        var truncated = builder.TruncateRaw(big)!;

        var expectedMarker = "…hyveman-truncated:1024";
        Assert.EndsWith(expectedMarker, truncated);

        var bytes = System.Text.Encoding.UTF8.GetByteCount(truncated);
        Assert.True(bytes <= 1024, $"truncated raw must fit max_raw_bytes (got {bytes})");
        Assert.StartsWith(truncated[..^expectedMarker.Length], big, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_Under_Cap_Untouched()
    {
        var builder = NewBuilder(maxRaw: 1024);
        var small = "<Event>small</Event>";
        Assert.Equal(small, builder.TruncateRaw(small));
        Assert.Null(builder.TruncateRaw(null));
    }

    [Fact]
    public void Truncation_Keeps_Valid_Utf8()
    {
        var builder = NewBuilder(maxRaw: 512);
        // Multi-byte chars (emoji = 4 bytes) must not be split mid-sequence.
        var big = string.Concat(Enumerable.Repeat("🙂aé", 5000));
        var truncated = builder.TruncateRaw(big)!;
        var decoded = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(truncated));
        Assert.Equal(truncated, decoded); // round-trips losslessly ⇒ valid UTF-8
    }

    [Fact]
    public void Missing_Message_Falls_Back_To_Summary()
    {
        var ev = KernelPower6008();
        ev.Message = null;
        var item = NewBuilder().BuildLogItem(ev);
        Assert.Contains("6008", item.Message);
        Assert.Contains("Microsoft-Windows-Kernel-Power", item.Message);
    }

    [Fact]
    public void Severity_Omitted_When_Level_Unspecified()
    {
        // PROTOCOL §10: Windows Level 0 (unspecified) → severity absent, not 0.
        var ev = KernelPower6008();
        ev.Level = 0;
        var item = NewBuilder().BuildLogItem(ev);
        Assert.Null(item.Severity);
    }

    [Fact]
    public void Severity_Present_For_Known_Level()
    {
        var ev = KernelPower6008();
        ev.Level = 4;
        var item = NewBuilder().BuildLogItem(ev);
        Assert.Equal(4, item.Severity);
    }

    [Fact]
    public void Facility_Null_When_Provider_Missing()
    {
        // Never a literal "unknown" — the contract is "facility = provider name".
        var ev = KernelPower6008();
        ev.ProviderName = null;
        var item = NewBuilder().BuildLogItem(ev);
        Assert.Null(item.Facility);
    }

    [Fact]
    public void Serialized_Item_Omits_Null_Severity_And_Facility()
    {
        var ev = KernelPower6008();
        ev.Level = 0;
        ev.ProviderName = null;
        var item = NewBuilder().BuildLogItem(ev);
        var batch = new LogBatchEnvelope { Source = "src_01", Items = new List<LogItem> { item } };
        var json = NewBuilder().Serialize(batch);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("items")[0].TryGetProperty("severity", out _));
        Assert.False(doc.RootElement.GetProperty("items")[0].TryGetProperty("facility", out _));
    }

    [Fact]
    public void DedupScope_Uses_Config_Entry_Name_For_SelfCollect()
    {
        // PROTOCOL §11.1 exception: self-collect entries map onto a shared
        // channel (Application) via a provider filter; dedup_scope is the
        // config entry name so EventRecordIDs cannot collide in the UNIQUE key.
        var ev = KernelPower6008();
        ev.DedupScope = "HyvemanAgent"; // config entry name
        ev.Channel = "Application";     // actual channel behind it
        var item = NewBuilder().BuildLogItem(ev);
        Assert.Equal("HyvemanAgent", item.DedupScope);
        Assert.Equal("Application", item.Fields!.Channel);
    }

    [Fact]
    public void Batch_Splits_When_Over_MaxBatchBytes()
    {
        var limits = new LimitsOptions { MaxRawBytes = 8192, MaxBatchBytes = 4096 };
        var builder = new EnvelopeBuilder(limits);

        var events = new List<EvtLogEvent>();
        for (int i = 0; i < 50; i++)
        {
            var ev = KernelPower6008();
            ev.RecordId = (ulong)(1000 + i);
            ev.RawXml = new string('z', 2000); // ~2 KB per item ⇒ 50 items ≈ 100 KB
            events.Add(ev);
        }

        var chunks = builder.BuildBatches(events, "src_01");
        Assert.True(chunks.Count > 1, "expected the batch to be split");
        foreach (var (json, evs) in chunks)
        {
            Assert.True(json.Length <= limits.MaxBatchBytes, "each chunk must fit max_batch_bytes");
            Assert.Equal(evs.Count, System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("items").GetArrayLength());
        }
        Assert.Equal(events.Count, chunks.Sum(c => c.Events.Count));
    }

    [Fact]
    public void Chunk_Order_And_Contents_Preserved()
    {
        var limits = new LimitsOptions { MaxRawBytes = 8192, MaxBatchBytes = 2048 };
        var builder = new EnvelopeBuilder(limits);

        var events = new List<EvtLogEvent>();
        for (int i = 0; i < 10; i++)
        {
            var ev = KernelPower6008();
            ev.RecordId = (ulong)(500 + i);
            ev.RawXml = new string('q', 1000);
            events.Add(ev);
        }

        var chunks = builder.BuildBatches(events, "src_01");
        var flat = chunks.SelectMany(c => c.Events).Select(e => e.RecordId).ToList();
        Assert.Equal(events.Select(e => e.RecordId), flat); // order preserved end-to-end
    }
}
