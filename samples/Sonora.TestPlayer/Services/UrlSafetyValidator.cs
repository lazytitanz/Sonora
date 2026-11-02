using System.Net;
using System.Net.Sockets;

namespace Sonora.TestPlayer.Services;

/// <summary>How <see cref="UrlSafetyValidator"/> treats loopback / private / link-local destinations.</summary>
public enum UrlSafetyMode
{
    /// <summary>Default. Hosts classified as local or private are denied.</summary>
    Restricted = 0,

    /// <summary>
    /// Explicit developer override. Local and private hosts are allowed (for example a loopback test server),
    /// still reported with a reason so the UI can show that the override was used.
    /// </summary>
    DeveloperAllowLocal = 1,
}

/// <summary>
/// Best-effort, <b>pre-flight</b> URL checks for the test player's streaming feature.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is NOT a complete SSRF defense.</b> Sonora's HTTP transport (<c>HttpAudioSource</c> /
/// <c>HttpStreamingOptions</c>) does not expose DNS-resolution or redirect hooks, so this class cannot
/// intercept what the engine actually connects to. In particular:
/// </para>
/// <list type="bullet">
/// <item><description>Host names are not resolved here. A public-looking name that resolves to a private address
/// (DNS rebinding, attacker-controlled DNS) passes validation.</description></item>
/// <item><description>HTTP redirects are followed by the engine (up to <c>MaxRedirects</c>) without consulting this
/// validator, so a public URL can redirect to a blocked destination.</description></item>
/// <item><description>Only literal IP hosts and well-known local name patterns are classified.</description></item>
/// </list>
/// <para>
/// It exists to stop obvious mistakes (typing <c>http://127.0.0.1/</c>, pasting an intranet URL) in a developer
/// tool and to make the risk visible, not to protect a service that accepts untrusted URLs.
/// The class is stateless and thread-safe.
/// </para>
/// </remarks>
public static class UrlSafetyValidator
{
    private static readonly string[] BlockedNameSuffixes =
    [
        ".localhost",
        ".local",
        ".internal",
        ".localdomain",
        ".home.arpa",
    ];

    /// <summary>Maps the UI flag to a <see cref="UrlSafetyMode"/>.</summary>
    public static UrlSafetyMode ModeFor(bool allowLocalDeveloperMode) =>
        allowLocalDeveloperMode ? UrlSafetyMode.DeveloperAllowLocal : UrlSafetyMode.Restricted;

    /// <summary>
    /// Validates a URI before it is handed to Sonora's HTTP source. See the class remarks for limitations.
    /// </summary>
    /// <param name="uri">URI to check.</param>
    /// <param name="allowLocalDeveloperMode">
    /// When <see langword="true"/>, hosts that would be blocked in <see cref="UrlSafetyMode.Restricted"/> mode are
    /// allowed. Scheme and credential checks still apply.
    /// </param>
    /// <returns><c>ok</c> indicates whether to proceed; <c>reason</c> always explains the decision.</returns>
    public static (bool ok, string reason) Validate(Uri uri, bool allowLocalDeveloperMode)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            return (false, "URL must be absolute.");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return (false, $"Unsupported scheme '{uri.Scheme}'. Only http and https are allowed.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return (false, "URLs containing credentials (user:password@host) are not allowed.");
        }

        var host = uri.IdnHost.Trim('[', ']');
        if (string.IsNullOrWhiteSpace(host))
        {
            return (false, "URL has no host.");
        }

        var blockedReason = ClassifyHost(host);
        if (blockedReason is null)
        {
            return (true, "Host is not classified as local or private (best-effort; DNS and redirects are not checked).");
        }

        return allowLocalDeveloperMode
            ? (true, $"Allowed by developer override: {blockedReason}.")
            : (false, $"Blocked: {blockedReason}. Enable the developer override to allow local hosts.");
    }

    /// <summary>
    /// Convenience overload that parses an absolute URI string.
    /// </summary>
    public static (bool ok, string reason) Validate(string url, bool allowLocalDeveloperMode)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return (false, "Value is not a valid absolute URL.");
        }

        return Validate(uri, allowLocalDeveloperMode);
    }

    /// <summary>Returns a description of why the host is considered local/private, or <see langword="null"/>.</summary>
    private static string? ClassifyHost(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return ClassifyAddress(address);
        }

        var name = host.TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0)
        {
            return "empty host name";
        }

        if (name == "localhost")
        {
            return "host is localhost";
        }

        foreach (var suffix in BlockedNameSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return $"host name ends with '{suffix}'";
            }
        }

        if (!name.Contains('.', StringComparison.Ordinal))
        {
            return "single-label host name (typically resolved on a local network)";
        }

        return null;
    }

    private static string? ClassifyAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => ClassifyIPv4(b),
            AddressFamily.InterNetworkV6 => ClassifyIPv6(address, b),
            _ => "unsupported address family",
        };
    }

    private static string? ClassifyIPv4(byte[] b)
    {
        if (b[0] == 0)
        {
            return "0.0.0.0/8 'this network' address";
        }

        if (b[0] == 10)
        {
            return "private address (10.0.0.0/8)";
        }

        if (b[0] == 100 && (b[1] & 0xC0) == 64)
        {
            return "carrier-grade NAT address (100.64.0.0/10)";
        }

        if (b[0] == 127)
        {
            return "loopback address (127.0.0.0/8)";
        }

        if (b[0] == 169 && b[1] == 254)
        {
            return "link-local address (169.254.0.0/16)";
        }

        if (b[0] == 172 && (b[1] & 0xF0) == 16)
        {
            return "private address (172.16.0.0/12)";
        }

        if (b[0] == 192 && b[1] == 168)
        {
            return "private address (192.168.0.0/16)";
        }

        if (b[0] == 192 && b[1] == 0 && b[2] == 0)
        {
            return "IETF protocol assignment address (192.0.0.0/24)";
        }

        if (b[0] == 198 && (b[1] & 0xFE) == 18)
        {
            return "benchmarking address (198.18.0.0/15)";
        }

        if (b[0] >= 224)
        {
            return "multicast or reserved address (224.0.0.0 and above)";
        }

        return null;
    }

    private static string? ClassifyIPv6(IPAddress address, byte[] b)
    {
        if (IPAddress.IPv6Loopback.Equals(address))
        {
            return "IPv6 loopback address (::1)";
        }

        if (IPAddress.IPv6Any.Equals(address))
        {
            return "IPv6 unspecified address (::)";
        }

        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)
        {
            return "IPv6 link-local address (fe80::/10)";
        }

        if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0)
        {
            return "IPv6 site-local address (fec0::/10)";
        }

        if ((b[0] & 0xFE) == 0xFC)
        {
            return "IPv6 unique-local address (fc00::/7)";
        }

        if (b[0] == 0xFF)
        {
            return "IPv6 multicast address (ff00::/8)";
        }

        return null;
    }
}
