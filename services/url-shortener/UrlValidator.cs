using System.Net;
using System.Net.Sockets;

namespace Service;

/// <summary>
/// Pure validation of candidate URLs: scheme allow-list and literal-IP private/loopback/
/// link-local range checks. Never performs DNS resolution — only literal IP hosts are checked
/// against ranges, per requirement.
/// </summary>
public static class UrlValidator
{
    private static readonly (IPAddress Network, int Prefix)[] BlockedV4 =
    {
        (IPAddress.Parse("127.0.0.0"), 8),   // loopback
        (IPAddress.Parse("169.254.0.0"), 16), // link-local
        (IPAddress.Parse("10.0.0.0"), 8),     // RFC 1918
        (IPAddress.Parse("172.16.0.0"), 12),  // RFC 1918
        (IPAddress.Parse("192.168.0.0"), 16), // RFC 1918
    };

    private static readonly (IPAddress Network, int Prefix)[] BlockedV6 =
    {
        (IPAddress.Parse("::1"), 128),   // loopback
        (IPAddress.Parse("fe80::"), 10), // link-local
        (IPAddress.Parse("fc00::"), 7),  // unique-local
    };

    /// <summary>Validates a candidate URL. On success, <paramref name="uri"/> holds the parsed URI.</summary>
    public static bool TryValidate(string? url, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "url is required";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            error = "url must be an absolute URI";
            return false;
        }

        if (!string.Equals(parsed.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            error = "url scheme must be http or https";
            return false;
        }

        UriHostNameType hostType = Uri.CheckHostName(parsed.Host);
        if ((hostType == UriHostNameType.IPv4 || hostType == UriHostNameType.IPv6) &&
            IPAddress.TryParse(parsed.Host, out IPAddress? ip) &&
            IsBlocked(ip))
        {
            error = "url host is not permitted";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsBlocked(IPAddress ip)
    {
        (IPAddress Network, int Prefix)[] ranges = ip.AddressFamily == AddressFamily.InterNetwork ? BlockedV4 : BlockedV6;
        foreach ((IPAddress network, int prefix) in ranges)
        {
            if (IsInRange(ip, network, prefix))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInRange(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        byte[] addressBytes = address.GetAddressBytes();
        byte[] networkBytes = network.GetAddressBytes();
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits > 0)
        {
            int mask = (byte)(0xFF << (8 - remainingBits));
            if ((addressBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
            {
                return false;
            }
        }

        return true;
    }
}