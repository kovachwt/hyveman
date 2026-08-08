using System.Security.Cryptography;
using System.Text;

namespace Hyveman.Server.Auth;

public static class TokenHasher
{
    /// <summary>SHA-256 of the complete raw token (including prefix). Raw tokens are never stored.</summary>
    public static string Hash(string rawToken)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    public static string NewRawToken(string prefix, int bytes = 32)
    {
        var buf = RandomNumberGenerator.GetBytes(bytes);
        return prefix + Base32UrlEncode(buf);
    }

    private static string Base32UrlEncode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }
}

/// <summary>SHA-256 of the session cookie payload (HMAC-signed session, §12.2).</summary>
public static class SessionCrypto
{
    public static byte[] DeriveKey(byte[] serverKey, string purpose)
        => SHA256.HashData(Encoding.UTF8.GetBytes(purpose).Concat(serverKey).ToArray());
}
