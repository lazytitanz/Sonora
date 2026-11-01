using ATL;

namespace Sonora.Audio.Metadata;

/// <summary>
/// Platform-neutral metadata reader backed by ATL.NET (<c>z440.atl.core</c>).
/// Does not initialize WASAPI, create an <see cref="AudioPlayer"/>, or decode PCM for playback.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safety: instances may be used concurrently for independent reads.
/// File handles are not retained after a call returns.
/// </para>
/// <para>
/// See <see cref="AtlSettingsPolicy"/> for the single ATL process-global setting Sonora applies.
/// </para>
/// </remarks>
public sealed class AudioMetadataReader : IAudioMetadataReader
{
    private static readonly AudioMetadataReadOptions DefaultOptions = new();

    /// <inheritdoc />
    public AudioTrackMetadata Read(string filePath, AudioMetadataReadOptions? options = null)
    {
        ValidatePath(filePath);
        options ??= DefaultOptions;
        AtlSettingsPolicy.EnsureApplied();

        try
        {
            EnsureFileExists(filePath);
            var track = new Track(filePath, load: true);
            return AtlMetadataMapper.MapTrack(track, options, includeArtwork: options.IncludeArtwork);
        }
        catch (Exception ex) when (ex is not AudioException
                                   and not ArgumentException
                                   and not FileNotFoundException
                                   and not DirectoryNotFoundException
                                   and not UnauthorizedAccessException
                                   and not IOException)
        {
            throw TranslateAtlFailure(filePath, ex);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new InvalidAudioException($"Unable to read audio metadata from '{filePath}'.", ex);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioArtwork> ReadArtwork(string filePath, AudioMetadataReadOptions? options = null)
    {
        ValidatePath(filePath);
        options ??= DefaultOptions;
        AtlSettingsPolicy.EnsureApplied();

        try
        {
            EnsureFileExists(filePath);

            // Avoid the Track constructor's full metadata Update(false) pass:
            // construct unloaded, then access EmbeddedPictures which triggers Update(true)
            // (picture-focused reload in ATL 7.19.0).
            var track = new Track(filePath, load: false);
            return AtlMetadataMapper.MapArtwork(track.EmbeddedPictures, options);
        }
        catch (Exception ex) when (ex is not AudioException
                                   and not ArgumentException
                                   and not FileNotFoundException
                                   and not DirectoryNotFoundException
                                   and not UnauthorizedAccessException
                                   and not IOException)
        {
            throw TranslateAtlFailure(filePath, ex);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new InvalidAudioException($"Unable to read audio artwork from '{filePath}'.", ex);
        }
    }

    private static void ValidatePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must be provided.", nameof(filePath));
        }
    }

    private static void EnsureFileExists(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);
        }
    }

    private static Exception TranslateAtlFailure(string filePath, Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        if (ContainsAny(message, "not supported", "unsupported", "unknown format", "cannot find", "no format"))
        {
            return new UnsupportedFormatException(
                $"Unsupported or unrecognized audio format: '{filePath}'.", ex);
        }

        if (ContainsAny(message, "corrupt", "truncated", "invalid", "end of stream", "unexpected"))
        {
            return new InvalidAudioException(
                $"Invalid or corrupt audio metadata in '{filePath}'.", ex);
        }

        // DummyReader / unreadable paths often surface as generic failures.
        if (ex is InvalidDataException or EndOfStreamException)
        {
            return new InvalidAudioException(
                $"Invalid or corrupt audio metadata in '{filePath}'.", ex);
        }

        return new InvalidAudioException(
            $"Failed to read audio metadata from '{filePath}'.", ex);
    }

    private static bool ContainsAny(string message, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (message.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
