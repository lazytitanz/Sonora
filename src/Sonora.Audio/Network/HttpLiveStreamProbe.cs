namespace Sonora.Audio.Network;

/// <summary>
/// Detects Icecast/SHOUTcast-style responses that advertise byte ranges incorrectly.
/// Those servers often answer <c>Range: bytes=0-0</c> with
/// <c>Content-Range: bytes 0-0/1073741823</c> even though the body is an unbounded live stream.
/// Treating that as a seekable resource causes ranged GETs that hang or return 403.
/// </summary>
internal static class HttpLiveStreamProbe
{
    /// <summary>Classic Icecast fake length (<c>2^30 - 1</c>) returned for live mounts.</summary>
    internal const long IcecastFakeLength = 1_073_741_823L;

    /// <summary>
    /// Returns <see langword="true"/> when a 206 probe should not be treated as random-access.
    /// </summary>
    public static bool IsNonSeekablePartialContent(HttpResponseMessage response, long? contentRangeTotal)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (HasIcyHeaders(response))
        {
            return true;
        }

        return contentRangeTotal == IcecastFakeLength;
    }

    private static bool HasIcyHeaders(HttpResponseMessage response)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.StartsWith("icy-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var header in response.Content.Headers)
        {
            if (header.Key.StartsWith("icy-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
