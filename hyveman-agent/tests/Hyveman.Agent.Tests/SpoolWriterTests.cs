using System.Text.Json;
using Hyveman.Agent.Pipeline;
using Hyveman.Agent.Wevtapi;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>
/// End-to-end spool pipeline tests: write → caps enforcement → read back,
/// drop-oldest on saturation, .tmp cleanup (AGENT.md §4.1, §19.A/§19.B #5).
/// </summary>
public class SpoolWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hyveman-spool-" + Guid.NewGuid().ToString("N"));

    public SpoolWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static Microsoft.Extensions.Logging.ILogger<SpoolWriter> Log() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<SpoolWriter>.Instance;

    private static byte[] Batch(int itemCount, string fill = "x")
    {
        var items = Enumerable.Range(0, itemCount)
            .Select(i => (object)new { kind = "log", record_id = i.ToString(), dedup_scope = "System" });
        return JsonSerializer.SerializeToUtf8Bytes(new { v = 1, items });
    }

    [Fact]
    public void Writes_And_Durably_Names_Files()
    {
        var monitor = new RuntimeMonitor();
        var caps = new SpoolCaps(1_000_000, 10_000);
        var writer = new SpoolWriter(_dir, caps, monitor, Log());
        writer.Initialize();

        var name = writer.WriteBatch(Batch(3), 3);
        Assert.NotNull(name);
        Assert.True(File.Exists(Path.Combine(_dir, name!)));
        Assert.Matches(@"^\d{13}-[0-9a-f]{5}\.json$", name!);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Saturation_Drops_Oldest_And_Counts()
    {
        var monitor = new RuntimeMonitor();
        var caps = new SpoolCaps(400, 10_000); // fits one ~275 B batch, not two
        var writer = new SpoolWriter(_dir, caps, monitor, Log());
        writer.Initialize();

        var first = writer.WriteBatch(Batch(5), 5)!;
        var second = writer.WriteBatch(Batch(5), 5);
        Assert.NotNull(second); // fits after dropping oldest

        Assert.False(File.Exists(Path.Combine(_dir, first))); // oldest dropped
        Assert.Equal(5, monitor.Snapshot().EventsDropped);
        Assert.Equal("spool_full", monitor.Degraded); // visible in the next heartbeats
    }

    [Fact]
    public void Degraded_Flag_Is_Reported_For_Ttl_Then_Self_Clears()
    {
        var monitor = new RuntimeMonitor(stickyTtlMs: 100);
        monitor.SetDegraded("spool_full");
        Assert.Equal("spool_full", monitor.Degraded);

        Thread.Sleep(200);
        Assert.Equal("", monitor.Degraded); // self-clears after the window
    }

    [Fact]
    public void Degraded_Priority_Spool_Full_Over_Wmi()
    {
        var monitor = new RuntimeMonitor(stickyTtlMs: 1000);
        monitor.SetDegraded("wmi_degraded");
        monitor.SetDegraded("spool_full");
        Assert.Equal("spool_full", monitor.Degraded);
        monitor.SetDegraded("overrun");
        Assert.Equal("spool_full", monitor.Degraded);
        monitor.ClearDegraded("spool_full");
        Assert.Equal("overrun", monitor.Degraded);
    }

    [Fact]
    public void Degraded_Priority_Auth_Rejected_Over_Wmi_And_Quarantined()
    {
        var monitor = new RuntimeMonitor(stickyTtlMs: 1000);
        monitor.SetDegraded("wmi_degraded");
        monitor.SetDegraded("auth_rejected");
        Assert.Equal("auth_rejected", monitor.Degraded);
        monitor.SetDegraded("quarantined");
        Assert.Equal("auth_rejected", monitor.Degraded); // credential problem ranks above a quarantined batch
    }

    [Fact]
    public void Write_Rejected_When_Even_Empty_Spool_Cannot_Fit()
    {
        var monitor = new RuntimeMonitor();
        // min_free floor above the volume's free space ⇒ nothing can ever be written.
        var caps = new SpoolCaps(1_000_000, long.MaxValue / 2);
        var writer = new SpoolWriter(_dir, caps, monitor, Log());
        writer.Initialize();

        var name = writer.WriteBatch(Batch(2), 2);
        Assert.Null(name);
        Assert.Equal(2, monitor.Snapshot().EventsDropped);
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public void Min_Free_Floor_Is_Enforced_Not_Just_Absolute_Cap()
    {
        var monitor = new RuntimeMonitor();
        // Absolute cap big; free-space floor effectively 0 ⇒ writes allowed.
        var caps = new SpoolCaps(1_000_000, 1);
        var writer = new SpoolWriter(_dir, caps, monitor, Log());
        writer.Initialize();
        Assert.NotNull(writer.WriteBatch(Batch(1), 1));
    }

    [Fact]
    public void Min_Free_Floor_Rejects_When_Volume_Too_Full()
    {
        var monitor = new RuntimeMonitor();
        var caps = new SpoolCaps(1_000_000, long.MaxValue / 2); // floor above any real volume free space
        var writer = new SpoolWriter(_dir, caps, monitor, Log());
        writer.Initialize();

        Assert.Null(writer.WriteBatch(Batch(1), 1));
        Assert.Equal(1, monitor.Snapshot().EventsDropped);
        Assert.Equal("spool_full", monitor.Degraded);
    }

    [Fact]
    public void Stale_Tmp_Cleaned_On_Init()
    {
        var monitor = new RuntimeMonitor();
        var caps = new SpoolCaps(1_000_000, 1);
        var writer = new SpoolWriter(_dir, caps, monitor, Log());

        var stale = Path.Combine(_dir, "1234-00001.json.tmp");
        File.WriteAllText(stale, "partial");
        writer.Initialize();

        Assert.False(File.Exists(stale)); // crash leftover removed, never a corrupt final file
    }

    [Fact]
    public void Min_Free_Floor_Enforced()
    {
        var monitor = new RuntimeMonitor();
        // Absolute cap small enough that two ~175 B batches don't fit.
        var caps = new SpoolCaps(300, 10_000);
        var writer = new SpoolWriter(_dir, caps, monitor, Log());
        writer.Initialize();

        var first = writer.WriteBatch(Batch(3), 3);
        Assert.NotNull(first);
        var second = writer.WriteBatch(Batch(3), 3);
        Assert.NotNull(second); // after drop-oldest
        Assert.Equal(3, monitor.Snapshot().EventsDropped);
    }

    [Fact]
    public void Bookmark_Advance_After_Spool_Ordering()
    {
        // The ordering rule (AGENT §6.6): bookmark reflects only what is on disk.
        var monitor = new RuntimeMonitor();
        var caps = new SpoolCaps(1_000_000, 1);
        var writer = new SpoolWriter(_dir, caps, monitor, Log());
        writer.Initialize();

        var ev = new EvtLogEvent
        {
            Channel = "System",
            DedupScope = "System",
            RecordId = 77,
            BookmarkXml = "<BookmarkList><Bookmark Channel='System' RecordId='77'/></BookmarkList>"
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            items = new object[]
            {
                new { kind = "log", record_id = "77", dedup_scope = "System" }
            }
        });

        Assert.NotNull(writer.WriteBatch(json, 1));

        var bookmarks = new BookmarkManager(Path.Combine(Path.GetTempPath(), "hyveman-state-" + Guid.NewGuid().ToString("N")), 
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BookmarkManager>.Instance);
        bookmarks.Initialize();
        bookmarks.Advance("System", ev, seq: 1);

        Assert.Equal(77, bookmarks.Load("System")!.RecordId);
    }
}
