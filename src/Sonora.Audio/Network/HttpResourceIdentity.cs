namespace Sonora.Audio.Network;

/// <summary>
/// Validators and length associated with an HTTP audio resource version.
/// Strong ETags are preferred; weak or missing validators are documented as less safe for seek/reconnect.
/// </summary>
internal sealed class HttpResourceIdentity
{
    public HttpResourceIdentity(long? contentLength, string? strongETag, string? lastModified)
    {
        ContentLength = contentLength;
        StrongETag = strongETag;
        LastModified = lastModified;
    }

    public long? ContentLength { get; }
    public string? StrongETag { get; }
    public string? LastModified { get; }

    public bool HasStrongValidator => !string.IsNullOrEmpty(StrongETag);

    public static string? ParseStrongETag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            // Weak validators must not be used with If-Range for byte stitching.
            return null;
        }

        return value;
    }

    public bool Matches(HttpResourceIdentity other)
    {
        if (ContentLength is long a && other.ContentLength is long b && a != b)
        {
            return false;
        }

        if (HasStrongValidator && other.HasStrongValidator
            && !string.Equals(StrongETag, other.StrongETag, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public HttpResourceIdentity WithLength(long? length) =>
        new(length ?? ContentLength, StrongETag, LastModified);
}
