using Hyveman.Agent.Pipeline;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>
/// Bookmark lifecycle tests (AGENT.md §6.6/§14): atomic persistence, corrupt
/// file fallback, channel-safe names, epoch persistence.
/// </summary>
public class BookmarkManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hyveman-tests-" + Guid.NewGuid().ToString("N"));

    public BookmarkManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ILogger<BookmarkManager> Log() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<BookmarkManager>.Instance;

    private static ILogger<EpochManager> EpochLog() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<EpochManager>.Instance;

    [Fact]
    public void RoundTrip_Persists_Bookmark_Xml_RecordId_And_Seq()
    {
        var mgr = new BookmarkManager(_dir, Log());
        mgr.Initialize();

        var ev = new Wevtapi.EvtLogEvent
        {
            Channel = "System",
            RecordId = 42,
            BookmarkXml = "<BookmarkList><Bookmark Channel='System' RecordId='42'/></BookmarkList>"
        };
        mgr.Advance("System", ev, seq: 3);

        var loaded = mgr.Load("System");
        Assert.NotNull(loaded);
        Assert.Equal("<BookmarkList><Bookmark Channel='System' RecordId='42'/></BookmarkList>", loaded!.BookmarkXml);
        Assert.Equal(42, loaded.RecordId);
        Assert.Equal(3, loaded.Seq);
    }

    [Fact]
    public void Missing_File_Returns_Null()
    {
        var mgr = new BookmarkManager(_dir, Log());
        Assert.Null(mgr.Load("System"));
    }

    [Fact]
    public void Corrupt_File_Returns_Null_And_Does_Not_Throw()
    {
        var mgr = new BookmarkManager(_dir, Log());
        File.WriteAllText(mgr.FilePathFor("System"), "{ this is not json");
        Assert.Null(mgr.Load("System"));
    }

    [Fact]
    public void Truncated_File_Returns_Null()
    {
        var mgr = new BookmarkManager(_dir, Log());
        File.WriteAllText(mgr.FilePathFor("System"), "{\"v\":1,\"bookmark_xml\":\"<BookmarkList");
        Assert.Null(mgr.Load("System"));
    }

    [Fact]
    public void Atomic_Write_Leaves_No_Tmp_Or_Corrupt_Final()
    {
        var mgr = new BookmarkManager(_dir, Log());
        var ev = new Wevtapi.EvtLogEvent { Channel = "System", RecordId = 7, BookmarkXml = "<BookmarkList/>" };
        mgr.Advance("System", ev, 1);
        Assert.False(File.Exists(mgr.FilePathFor("System") + ".tmp"));
        Assert.NotNull(mgr.Load("System"));
    }

    [Fact]
    public void Channel_Safe_Names_Sanitize_Invalid_Chars()
    {
        Assert.Equal("Microsoft-Windows-Hyper-V-VMMS-Admin", SpoolFiles.ChannelSafeName("Microsoft-Windows-Hyper-V-VMMS-Admin"));
        Assert.Equal("Weird_Name_with_chars", SpoolFiles.ChannelSafeName("Weird/Name:with*chars"));
        var name = SpoolFiles.ChannelSafeName("a/b:c*d?e\"f<g>h|i");
        foreach (var c in Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(c, name);
    }

    [Fact]
    public void LastPositionedEvent_Skips_Synthetic_Events()
    {
        // P2-3: synthetic events (channel_reset, no BookmarkXml) must not
        // block the bookmark advance — pick the last event with a position.
        var real1 = new Wevtapi.EvtLogEvent { Channel = "System", RecordId = 1, BookmarkXml = "<BookmarkList><Bookmark Channel='System' RecordId='1'/></BookmarkList>" };
        var synthetic = new Wevtapi.EvtLogEvent { Channel = "System", RecordId = 0, Epoch = 1 }; // no BookmarkXml
        var real2 = new Wevtapi.EvtLogEvent { Channel = "System", RecordId = 2, BookmarkXml = "<BookmarkList><Bookmark Channel='System' RecordId='2'/></BookmarkList>" };

        Assert.Same(real2, BookmarkManager.LastPositionedEvent(new[] { real1, synthetic, real2 }));
        Assert.Same(real2, BookmarkManager.LastPositionedEvent(new[] { real1, real2, synthetic })); // synthetic last → still advances to real2
        Assert.Null(BookmarkManager.LastPositionedEvent(new[] { synthetic }));                       // only synthetic → nothing to advance
    }

    [Fact]
    public void Epoch_Persists_And_RoundTrips()
    {
        var mgr = new EpochManager(_dir, EpochLog());
        Assert.Equal(0, mgr.Load("System")); // none yet
        mgr.Save("System", 3);
        Assert.Equal(3, mgr.Load("System"));
        mgr.Save("System", 4); // increments per reset
        Assert.Equal(4, mgr.Load("System"));
    }

    [Fact]
    public void Spool_Filename_Format_And_Order()
    {
        var a = SpoolFiles.NewFileName();
        var b = SpoolFiles.NewFileName();
        // <unixms>-<hexseq>.json, lexicographic order == chronological.
        Assert.Matches(@"^\d{13}-[0-9a-f]{5}\.json$", a);
        Assert.True(string.CompareOrdinal(a, b) <= 0);
        Assert.True(SpoolFiles.IsSpoolFile(a));
        Assert.False(SpoolFiles.IsSpoolFile(a + ".tmp"));
    }
}
