namespace Sonora.Audio.Metadata;

/// <summary>
/// Reads track metadata and optional embedded artwork without creating an audio player,
/// initializing output devices, or decoding PCM for playback.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must dispose file handles before returning. Concurrent independent
/// <see cref="Read"/> / <see cref="ReadArtwork"/> calls on the same instance are safe.
/// </para>
/// <para>
/// Local-file extraction is synchronous I/O. Applications that need parallel library scans
/// should fan out calls themselves; Sonora does not introduce internal unbounded parallelism.
/// </para>
/// </remarks>
public interface IAudioMetadataReader
{
    /// <summary>
    /// Reads metadata from a local audio file.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to a local audio file.</param>
    /// <param name="options">
    /// Optional read options. When <see langword="null"/>, defaults are used
    /// (<see cref="AudioMetadataReadOptions.IncludeArtwork"/> is <see langword="false"/>).
    /// </param>
    /// <returns>
    /// Normalized metadata. A valid audio file with no tags returns unavailable fields
    /// rather than throwing.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or whitespace.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be opened for reading.</exception>
    /// <exception cref="UnsupportedFormatException">The format is not a supported audio container.</exception>
    /// <exception cref="InvalidAudioException">The file is corrupt, truncated, or otherwise unreadable as audio metadata.</exception>
    AudioTrackMetadata Read(string filePath, AudioMetadataReadOptions? options = null);

    /// <summary>
    /// Reads embedded artwork only, without requiring a prior <see cref="Read"/> that included artwork.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to a local audio file.</param>
    /// <param name="options">Optional limits for artwork size and count. <see cref="AudioMetadataReadOptions.IncludeArtwork"/> is ignored.</param>
    /// <returns>Owned artwork copies. Empty when none are present or all exceed limits.</returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or whitespace.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be opened for reading.</exception>
    /// <exception cref="UnsupportedFormatException">The format is not a supported audio container.</exception>
    /// <exception cref="InvalidAudioException">The file is corrupt, truncated, or otherwise unreadable.</exception>
    IReadOnlyList<AudioArtwork> ReadArtwork(string filePath, AudioMetadataReadOptions? options = null);
}
