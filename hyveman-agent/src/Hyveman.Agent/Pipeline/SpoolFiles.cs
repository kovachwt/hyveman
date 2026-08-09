namespace Hyveman.Agent.Pipeline;

/// <summary>Spool file naming & enumeration (AGENT.md §14: &lt;unixms&gt;-&lt;hexseq&gt;.json).</summary>
public static class SpoolFiles
{
    private static long _seq;

    /// <summary>Lexicographic order == chronological order (ms prefix, then hex seq).</summary>
    public static string NewFileName()
    {
        var unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seq = Interlocked.Increment(ref _seq);
        return $"{unixMs}-{seq:x5}.json";
    }

    public static bool IsSpoolFile(string fileName)
        => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
           !fileName.StartsWith(".", StringComparison.Ordinal) &&
           !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Characters never legal in a Windows file name: control chars 0–31 plus
    /// &lt; &gt; : " / \ | ? *. Hardcoded rather than
    /// <c>Path.GetInvalidFileNameChars()</c> because that API is
    /// platform-dependent — on Linux it would allow ':', '*', … — which would
    /// make bookmark/epoch file names (and their tests) differ by OS. The
    /// agent is Windows-only, so the Windows set is the contract.
    /// </summary>
    private static readonly string InvalidChannelFileNameChars =
        new string(Enumerable.Range(0, 32).Select(i => (char)i).ToArray()) + "<>:\"/\\|?*";

    public static string ChannelSafeName(string channel)
    {
        var sb = new System.Text.StringBuilder(channel.Length);
        foreach (var c in channel)
            sb.Append(InvalidChannelFileNameChars.IndexOf(c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}

/// <summary>Reads the spool directory state (bytes/files) for heartbeat + caps.</summary>
public static class SpoolDirectory
{
    public static (long Bytes, int Files) Measure(string spoolDir)
    {
        long bytes = 0;
        int files = 0;
        if (!Directory.Exists(spoolDir))
            return (0, 0);

        try
        {
            foreach (var f in Directory.EnumerateFiles(spoolDir, "*.json"))
            {
                if (!SpoolFiles.IsSpoolFile(Path.GetFileName(f)))
                    continue;
                try
                {
                    bytes += new FileInfo(f).Length;
                    files++;
                }
                catch (IOException)
                {
                    // file raced with deletion; ignore
                }
                catch (UnauthorizedAccessException)
                {
                    // ignore
                }
            }
        }
        catch (Exception)
        {
            // never let directory enumeration break the heartbeat
        }
        return (bytes, files);
    }

    public static long VolumeFreeBytes(string spoolDir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(spoolDir));
            if (root is null) return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return long.MaxValue; // unknown free space ⇒ don't block writes on this cap
        }
    }

    public static IEnumerable<string> OldestFirst(string spoolDir)
    {
        if (!Directory.Exists(spoolDir))
            yield break;
        foreach (var f in Directory.EnumerateFiles(spoolDir, "*.json")
                     .Where(f => SpoolFiles.IsSpoolFile(Path.GetFileName(f)))
                     .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            yield return f;
    }
}
