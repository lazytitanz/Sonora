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
            // Fall back to forward-only progressive stream when ranges are unavailable.
        }

        var forward = new HttpContentByteStream(http.Uri, _httpOptions);
        var transferred = false;
        try
        {
            await forward.OpenAsync(cancellationToken).ConfigureAwait(false);
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
        finally
        {
            if (!transferred)
            {
                await forward.DisposeAsync().ConfigureAwait(false);
            }
        }
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
