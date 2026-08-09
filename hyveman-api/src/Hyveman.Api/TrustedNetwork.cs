using System.Net;
using System.Net.Sockets;

namespace Hyveman.Api;

/// <summary>First-run setup network gate (API.md §8.1): registration options are
/// permitted unauthenticated only from localhost/trusted networks.</summary>
public static class TrustedNetwork
{
    public static Func<string?, bool> Create(string[] cidrs)
    {
        var networks = cidrs
            .Select(Parse)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .ToList();
        return remoteIp =>
        {
            if (string.IsNullOrEmpty(remoteIp)) return false;
            if (!IPAddress.TryParse(remoteIp, out var ip)) return false;
            if (IPAddress.IsLoopback(ip)) return true; // localhost is always trusted
            return networks.Any(n => n.Contains(ip));
        };
    }

    private static (IPAddress Network, int Prefix)? Parse(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) ||
            !int.TryParse(parts[1], out var prefix))
            return null;
        return (ip, prefix);
    }
}

file static class NetworkExtensions
{
    public static bool Contains(this (IPAddress Network, int Prefix) network, IPAddress address)
    {
        var netBytes = network.Network.GetAddressBytes();
        var addrBytes = address.GetAddressBytes();
        if (netBytes.Length != addrBytes.Length) return false;
        var prefixBytes = network.Prefix / 8;
        var prefixBits = network.Prefix % 8;
        for (var i = 0; i < prefixBytes; i++)
            if (netBytes[i] != addrBytes[i]) return false;
        if (prefixBits > 0)
        {
            var mask = (byte)(0xFF << (8 - prefixBits));
            if ((netBytes[prefixBytes] & mask) != (addrBytes[prefixBytes] & mask)) return false;
        }
        return true;
    }
}
