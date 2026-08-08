namespace Hyveman.Agent.Pipeline;

/// <summary>
/// The two spool caps, both enforced before every write (AGENT.md §4.1, H1):
///   1. total bytes never exceed max_bytes;
///   2. a write must never push the volume's free space below min_free_bytes.
/// Pure logic — unit/property tested (§19.A).
/// </summary>
public sealed class SpoolCaps
{
    private readonly long _maxBytes;
    private readonly long _minFreeBytes;

    public SpoolCaps(long maxBytes, long minFreeBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (minFreeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(minFreeBytes));
        _maxBytes = maxBytes;
        _minFreeBytes = minFreeBytes;
    }

    public long MaxBytes => _maxBytes;
    public long MinFreeBytes => _minFreeBytes;

    /// <summary>
    /// True if a write of <paramref name="writeBytes"/> passes both caps given
    /// the current spool total and the volume's current free space.
    /// </summary>
    public bool WouldAllow(long currentTotalBytes, long currentFreeBytes, long writeBytes)
    {
        if (writeBytes < 0) throw new ArgumentOutOfRangeException(nameof(writeBytes));
        if (currentTotalBytes < 0 || currentFreeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(currentTotalBytes));
        if (currentTotalBytes + writeBytes > _maxBytes)
            return false;
        if (currentFreeBytes - writeBytes < _minFreeBytes)
            return false;
        return true;
    }
}
