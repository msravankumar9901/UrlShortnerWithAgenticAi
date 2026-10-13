using System.Net;
using System.Net.Sockets;

namespace UrlShortener.Core;

/// <summary>
/// Accepts only absolute http/https URLs and rejects destinations that would make a
/// later server-side fetch unsafe. Redirect handling itself never fetches the target.
/// Hostnames are not DNS-resolved: that would be a network call on the hot path and
/// its own SSRF vector. IP literals and well-known local names are blocked instead.
/// </summary>
public static class UrlValidator
{
    public const int MaxUrlLength = 2048;

    public static UrlValidationResult Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return UrlValidationResult.Invalid("URL is required.");

        var trimmed = url.Trim();
        if (trimmed.Length > MaxUrlLength)
            return UrlValidationResult.Invalid($"URL exceeds {MaxUrlLength} characters.");

        if (trimmed.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return UrlValidationResult.Invalid("URL contains illegal characters.");

        var schemeSep = trimmed.IndexOf(':');
        if (schemeSep <= 0)
            return UrlValidationResult.Invalid("Only absolute http and https URLs are allowed.");

        var scheme = trimmed[..schemeSep];
        if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return UrlValidationResult.Invalid("Only absolute http and https URLs are allowed.");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return UrlValidationResult.Invalid("URL is not a valid absolute URI.");

        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return UrlValidationResult.Invalid("Only absolute http and https URLs are allowed.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return UrlValidationResult.Invalid("Credentials embedded in the URL are not allowed.");

        if (IsBlockedHost(uri.IdnHost) || IsBlockedHost(uri.Host))
            return UrlValidationResult.Invalid("Destination host is not allowed (localhost, private, or link-local address).");

        return UrlValidationResult.Ok();
    }

    public static string Normalize(string url) => url.Trim();

    private static bool IsBlockedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        host = host.Trim().TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost.localdomain", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            return true;

        if (host.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return true;

        if (host.Length > 0 && host.All(char.IsDigit))
            return true;

        if (IPAddress.TryParse(host, out var ip))
            return IsBlockedAddress(ip);

        return false;
    }

    private static bool IsBlockedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127)
                return true;
            if (b[0] == 169 && b[1] == 254)
                return true;
            if (b[0] == 172 && b[1] is >= 16 and <= 31)
                return true;
            if (b[0] == 192 && b[1] == 168)
                return true;
            if (b[0] == 100 && b[1] is >= 64 and <= 127)
                return true;
            return false;
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
            return true;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC)
            return true;

        return false;
    }
}
