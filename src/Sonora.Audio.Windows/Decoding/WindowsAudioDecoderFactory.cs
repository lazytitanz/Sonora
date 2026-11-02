using Sonora.Audio.Abstractions;
using Sonora.Audio.Network;
using Sonora.Audio.Sources;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Creates Windows-backed <see cref="IAudioDecoder"/> instances for local files and HTTP sources.
/// </summary>
public sealed class WindowsAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly HttpStreamingOptions _httpOptions;

    /// <summary>
    /// Creates a factory using optional HTTP streaming defaults.
    /// </summary>
    /// <param name="options">HTTP options. When null, <see cref="HttpStreamingOptions.Default"/> is used.</param>
    public WindowsAudioDecoderFactory(HttpStreamingOptions? options = null)
    {
        _httpOptions = (options ?? HttpStreamingOptions.Default).Validate();
    }

    /// <inheritdoc />
    public Task<IAudioDecoder> CreateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IAudioDecoder decoder = WindowsAudioDecoder.Open(filePath);
            return decoder;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IAudioDecoder> CreateAsync(AudioSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            FileAudioSource file => CreateAsync(file.Path, cancellationToken),
            HttpAudioSource http => CreateHttpAsync(http, cancellationToken),
            _ => Task.FromException<IAudioDecoder>(new NotSupportedException(
                $"Audio source type '{source.GetType().Name}' is not supported by this decoder factory.")),
        };
    }

    private async Task<IAudioDecoder> CreateHttpAsync(HttpAudioSource http, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Prefer range-verified random-access stream (required for Media Foundation).
        try
        {
            var random = await HttpRandomAccessStream.OpenAsync(http.Uri, _httpOptions, cancellationToken)
                .ConfigureAwait(false);
            return await OpenOwnedAsync(
                    random,
                    http.Uri.AbsoluteUri,
                    random.ContentType,
                    d => NetworkTransportDecoder.ForRandomAccess(random, d),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NetworkAudioException)
        {
            // Fall back when ranges are unavailable or the server lied about Content-Range (live radio).
        }

        // Progressive forward-only works for some containers (e.g. Ogg); MF still needs seekable bytes.
        var forward = new HttpContentByteStream(http.Uri, _httpOptions);
        var transferred = false;
        NotSupportedException? nonSeekable = null;
        try
        {
            await forward.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var decoder = await OpenOwnedAsync(
                        forward,
                        http.Uri.AbsoluteUri,
                        forward.ContentType,
                        d => NetworkTransportDecoder.ForHttpContent(forward, d),
                        cancellationToken)
                    .ConfigureAwait(false);
                transferred = true;
                return decoder;
            }
            catch (NotSupportedException ex)
            {
                // MF rejected the non-seekable byte stream — try MF's own URL HTTP stack below.
                nonSeekable = ex;
            }
        }
        finally
        {
            if (!transferred)
            {
                await forward.DisposeAsync().ConfigureAwait(false);
            }
        }

        // Live Icecast/SHOUTcast MP3 mounts: Media Foundation can open the URL directly.
        try
        {
            return await OpenMediaFoundationUrlAsync(http.Uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException
            && ex is not ObjectDisposedException
            && nonSeekable is not null)
        {
            throw new Sonora.Audio.UnsupportedFormatException(
                $"The audio format of '{http.Uri.AbsoluteUri}' is not supported over a non-seekable HTTP stream.",
                nonSeekable);
        }
    }

    private static async Task<IAudioDecoder> OpenMediaFoundationUrlAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IAudioDecoder decoder = WaveStreamAudioDecoder.OpenMediaFoundationUrl(uri.AbsoluteUri);
                return (IAudioDecoder)NetworkTransportDecoder.ForExternalReady(decoder);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IAudioDecoder> OpenOwnedAsync(
        Stream stream,
        string origin,
        string? contentType,
        Func<IAudioDecoder, IAudioDecoder> wrap,
        CancellationToken cancellationToken)
    {
        var ownershipTransferred = false;
        try
        {
            var decoder = await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return WindowsAudioDecoder.Open(stream, origin, contentType);
                },
                cancellationToken).ConfigureAwait(false);

            ownershipTransferred = true;
            return wrap(decoder);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
