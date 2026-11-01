namespace Sonora.Audio.Network;

/// <summary>Redacts sensitive URI components for exceptions and diagnostics.</summary>
internal static class HttpUriSafety
{
    public static string Describe(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        // Omit query/fragment — may contain signed tokens.
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    public static bool IsSameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
