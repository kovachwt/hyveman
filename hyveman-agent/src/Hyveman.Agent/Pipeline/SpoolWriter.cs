using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hyveman.Agent.Pipeline;

/// <summary>
/// Durable spool writer (AGENT.md §5.1, §6.5, §14): each batch is one file,
/// written atomically (.tmp → flush → rename) after passing both spool caps.
/// On cap violation: delete oldest files and retry; if still over, the batch
/// is rejected and counted (never the straw that fills the disk — H1).
/// </summary>
public sealed class SpoolWriter
{
    private readonly string _spoolDir;
    private readonly SpoolCaps _caps;
    private readonly RuntimeMonitor _monitor;
    private readonly ILogger<SpoolWriter> _log;

    public SpoolWriter(string spoolDir, SpoolCaps caps, RuntimeMonitor monitor, ILogger<SpoolWriter> log)
    {
        _spoolDir = spoolDir;
        _caps = caps;
        _monitor = monitor;
        _log = log;
    }

    public string SpoolDir => _spoolDir;

    public void Initialize()
    {
        Directory.CreateDirectory(_spoolDir);
        // Clean .tmp leftovers from a prior crash (never a corrupt final file).
        foreach (var tmp in Directory.EnumerateFiles(_spoolDir, "*.tmp"))
        {
            try
            {
                File.Delete(tmp);
                _log.LogInformation("Cleaned stale spool temp file {file}", Path.GetFileName(tmp));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to clean stale spool temp file {file}", Path.GetFileName(tmp));
            }
        }
    }

    /// <summary>
    /// Durably writes one batch file. Returns the file name on success or null
    /// when the caps could not be satisfied even after dropping oldest files.
    /// </summary>
    public string? WriteBatch(byte[] batchJson, int itemCount)
    {
        var fileName = SpoolFiles.NewFileName();
        var finalPath = Path.Combine(_spoolDir, fileName);

        if (!EnsureCapacity(batchJson.Length))
        {
            _monitor.AddEventsDropped(itemCount);
            _monitor.SetDegraded("spool_full");
            _log.LogWarning("Spool cap check failed after dropping oldest; dropping batch of {count} events (spool_full)", itemCount);
            return null;
        }

        var tmpPath = finalPath + ".tmp";
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                fs.Write(batchJson, 0, batchJson.Length);
                fs.Flush(flushToDisk: true); // fsync — durable before rename
            }

            File.Move(tmpPath, finalPath); // atomic rename on same volume
            return fileName;
        }
        catch (Exception ex)
        {
            _monitor.AddEventsDropped(itemCount);
            _monitor.SetDegraded("spool_full");
            _log.LogError(ex, "Spool write failed for batch of {count} events; dropping", itemCount);
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
            return null;
        }
    }

    /// <summary>
    /// Enforces both caps before a write: drop oldest spool files until the
    /// write fits (counting their events), or return false if it never can.
    /// </summary>
    private bool EnsureCapacity(long writeBytes)
    {
        while (true)
        {
            var (total, _) = SpoolDirectory.Measure(_spoolDir);
            var free = SpoolDirectory.VolumeFreeBytes(_spoolDir);

            if (_caps.WouldAllow(total, free, writeBytes))
                return true;

            // Drop the oldest spool file and re-check.
            var oldest = SpoolDirectory.OldestFirst(_spoolDir).FirstOrDefault();
            if (oldest is null)
                return false;

            var droppedEvents = CountEvents(oldest);
            try
            {
                File.Delete(oldest);
                if (droppedEvents > 0)
                {
                    _monitor.AddEventsDropped(droppedEvents);
                    _monitor.SetDegraded("spool_full");
                    _log.LogWarning("Spool cap pressure: dropped oldest spool file {file} ({count} events)", Path.GetFileName(oldest), droppedEvents);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to drop oldest spool file {file}", Path.GetFileName(oldest));
                return false; // can't free space ⇒ reject the write
            }
        }
    }

    private static int CountEvents(string spoolFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(spoolFile));
            return doc.RootElement.TryGetProperty("items", out var items) ? items.GetArrayLength() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
