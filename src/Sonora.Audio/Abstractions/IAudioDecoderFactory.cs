using Sonora.Audio.Sources;

namespace Sonora.Audio.Abstractions;

/// <summary>
/// Creates <see cref="IAudioDecoder"/> instances for audio sources.
/// </summary>
public interface IAudioDecoderFactory
{
    /// <summary>
    /// Opens a decoder for the audio file at <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to an audio file.</param>
    /// <param name="cancellationToken">Token used to cancel preparation work.</param>
    /// <returns>A decoder that owns any underlying file handles.</returns>
    Task<IAudioDecoder> CreateAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a decoder for the specified <paramref name="source"/>.
    /// Default implementation supports <see cref="FileAudioSource"/> only; platform factories override for HTTP.
    /// </summary>
    Task<IAudioDecoder> CreateAsync(AudioSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            FileAudioSource file => CreateAsync(file.Path, cancellationToken),
            _ => throw new NotSupportedException(
                $"Audio source type '{source.GetType().Name}' is not supported by this decoder factory."),
        };
    }
}
