namespace Sonora.Audio.Sources;

/// <summary>HTTP or HTTPS audio source. Transport is separate from decoding.</summary>
/// <param name="Uri">Absolute http/https URI.</param>
public sealed record HttpAudioSource(Uri Uri) : AudioSource
{
    /// <inheritdoc />
    public override string Identifier => Uri.AbsoluteUri;

    /// <summary>Creates an HTTP source after validating an absolute http/https URI.</summary>
    public static HttpAudioSource Create(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("HTTP audio sources require an absolute URI.", nameof(uri));
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                $"Unsupported URI scheme '{uri.Scheme}'. Only http and https are supported.",
                nameof(uri));
        }

        return new HttpAudioSource(uri);
    }

    /// <summary>Parses and validates an absolute http/https URI string.</summary>
    public static HttpAudioSource Create(string uriString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriString);
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The value is not an absolute URI.", nameof(uriString));
        }

        return Create(uri);
    }
}
