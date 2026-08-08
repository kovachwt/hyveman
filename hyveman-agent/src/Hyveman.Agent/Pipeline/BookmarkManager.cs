using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Hyveman.Agent.Wevtapi;

namespace Hyveman.Agent.Pipeline;

/// <summary>
/// Per-channel bookmark persistence (AGENT.md §6.6): the bookmark is advanced
/// only after a batch containing that channel's events is durably spooled.
/// File: state\&lt;channel-safe&gt;.bookmark — JSON with the serialized bookmark
/// XML + EventRecordID + monotonic seq; atomic (temp + rename).
/// </summary>
public sealed class BookmarkManager
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _stateDir;
    private readonly ILogger<BookmarkManager> _log;

    public BookmarkManager(string stateDir, ILogger<BookmarkManager> log)
    {
        _stateDir = stateDir;
        _log = log;
    }

    public string FilePathFor(string channel)
        => Path.Combine(_stateDir, SpoolFiles.ChannelSafeName(channel) + ".bookmark");

    public void Initialize() => Directory.CreateDirectory(_stateDir);

    /// <summary>Loads the persisted bookmark for a channel (null when none / corrupt).</summary>
    public BookmarkState? Load(string channel)
    {
        var path = FilePathFor(channel);
        if (!File.Exists(path))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize<BookmarkState>(File.ReadAllText(path), JsonOpts);
            if (state is null || string.IsNullOrEmpty(state.BookmarkXml) || state.RecordId < 0)
                return null;
            return state;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Bookmark file for channel {channel} is corrupt; ignoring (resubscribe from now)", channel);
            return null;
        }
    }

    /// <summary>
    /// Advances a channel's bookmark to the position of the last event of that
    /// channel in a durably-spooled batch. Caller guarantees the batch was
    /// spooled first (AGENT §6.6 ordering rule).
    /// </summary>
    public void Advance(string channel, EvtLogEvent lastEventInBatch, long seq)
    {
        if (string.IsNullOrEmpty(lastEventInBatch.BookmarkXml))
            return; // no position captured — replay is deduped anyway

        var state = new BookmarkState
        {
            BookmarkXml = lastEventInBatch.BookmarkXml,
            RecordId = (long)lastEventInBatch.RecordId,
            Seq = seq,
            UpdatedAt = DateTime.UtcNow
        };

        var path = FilePathFor(channel);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts), new UTF8Encoding(false));
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.Flush(flushToDisk: true);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist bookmark for channel {channel}", channel);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
    /// <summary>
    /// The event whose bookmark position should be persisted for a group:
    /// the last event that actually carries a bookmark XML. Synthetic events
    /// (e.g. channel_reset, RecordId=0) have no position and must not block
    /// the advance (SPEC-DEVIATIONS P2-3). Null when no event has a position.
    /// </summary>
    public static EvtLogEvent? LastPositionedEvent(IEnumerable<EvtLogEvent> events)
        => events.Where(e => !string.IsNullOrEmpty(e.BookmarkXml)).LastOrDefault();
}

public sealed class BookmarkState
{
    public int V { get; set; } = 1;
    public string BookmarkXml { get; set; } = "";
    public long RecordId { get; set; }
    public long Seq { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Per-channel reset epoch (AGENT.md §6.7 / PROTOCOL §11.1): state\&lt;channel&gt;.epoc,
/// an integer persisted atomically. record_id = "e&lt;epoch&gt;:&lt;id&gt;" after a reset.
/// </summary>
public sealed class EpochManager
{
    private readonly string _stateDir;
    private readonly ILogger<EpochManager> _log;

    public EpochManager(string stateDir, ILogger<EpochManager> log)
    {
        _stateDir = stateDir;
        _log = log;
    }

    public string FilePathFor(string channel)
        => Path.Combine(_stateDir, SpoolFiles.ChannelSafeName(channel) + ".epoc");

    public int Load(string channel)
    {
        try
        {
            var path = FilePathFor(channel);
            if (!File.Exists(path))
                return 0;
            return int.TryParse(File.ReadAllText(path).Trim(), out var e) ? e : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public void Save(string channel, int epoch)
    {
        var path = FilePathFor(channel);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.Flush(flushToDisk: true);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist epoch for channel {channel}", channel);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
