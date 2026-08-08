using System.Security.Cryptography;

namespace Hyveman.Server.Common;

/// <summary>
/// Minimal ULID implementation (Crockford base32, 26 chars, ms-timestamp-prefixed, sortable).
/// IDs in the schema are TEXT like "src_<ulid>", "tok_<ulid>", "host_<ulid>".
/// </summary>
public static class Ulid
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly byte[] RandomBuf = new byte[10];

    public static string New()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RandomNumberGenerator.Fill(RandomBuf);
        Span<char> chars = stackalloc char[26];
        // 48-bit timestamp (6 bytes) → first 10 Crockford chars.
        var time = ((ulong)ts) << 16;
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Crockford[(int)(time & 0x1F)];
            time >>= 5;
        }
        // 80 random bits → last 16 chars.
        ulong r0 = ((ulong)RandomBuf[0] << 56) | ((ulong)RandomBuf[1] << 48) | ((ulong)RandomBuf[2] << 40) | ((ulong)RandomBuf[3] << 32)
                 | ((ulong)RandomBuf[4] << 24) | ((ulong)RandomBuf[5] << 16) | ((ulong)RandomBuf[6] << 8) | RandomBuf[7];
        ulong r1 = ((ulong)RandomBuf[8] << 8) | RandomBuf[9];
        for (var i = 25; i >= 10; i--)
        {
            chars[i] = Crockford[(int)(r0 & 0x1F)];
            r0 >>= 5;
            if (i == 10) { r0 = r1; }
        }
        return new string(chars);
    }

    public static string Prefixed(string prefix) => prefix + New();
}

/// <summary>UTC ISO-8601 helpers — the server's canonical timestamp format.</summary>
public static class WireTime
{
    public static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    public static string NowMs() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>Parse an agent-supplied UTC ISO-8601 timestamp; null if unparseable / not explicit-UTC.</summary>
    public static bool TryParseUtc(string? s, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return false;
        // Require an explicit zone designator (Z or ±hh:mm); a bare local time is not UTC ISO-8601.
        var hasZone = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || s.Contains('+') && s.Length > 10
            || s.Length > 10 && s[^6] == '-';
        if (!hasZone) return false;
        result = dt.ToUniversalTime();
        return true;
    }

    public static string ToIsoMs(DateTimeOffset dt) => dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
