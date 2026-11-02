using Sonora.Audio;
using Sonora.Audio.Metadata;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Services;

/// <summary>
/// Application-side wrapper over <see cref="IAudioMetadataReader"/>. Sonora never invents a title from
/// the file name; this facade does, for display only. Artwork is converted to a size-limited
/// <c>data:</c> URL for the web UI.
/// </summary>
/// <remarks>
/// <para>Thread-safety: stateless apart from the injected reader, which supports concurrent reads.</para>
/// <para>Reads are synchronous file I/O; callers on a UI thread should offload batches.</para>
/// </remarks>
public sealed class MetadataFacade
{
    /// <summary>Maximum encoded artwork size forwarded to the UI (2 MiB).</summary>
    public const int MaxArtworkBytesForUi = 2 * 1024 * 1024;

    private const int MaxArtworkCandidates = 4;

    private readonly IAudioMetadataReader _reader;
    private readonly Action<string, Exception>? _onReadFailure;

    /// <summary>Creates a facade.</summary>
    /// <param name="reader">Metadata reader (owned by the caller).</param>
    /// <param name="onReadFailure">
    /// Optional callback invoked with the file path and the exception when metadata cannot be read. The item is
    /// still produced with filename-derived fields so playback can be attempted.
    /// </param>
    public MetadataFacade(IAudioMetadataReader reader, Action<string, Exception>? onReadFailure = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _onReadFailure = onReadFailure;
    }

    /// <summary>
    /// Builds a queue item for a local file. Metadata failures (unsupported/corrupt tags) fall back to
    /// filename-derived values; the failure is reported through the constructor callback.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or whitespace.</exception>
    public QueueItem CreateQueueItem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);

        try
        {
            var meta = _reader.Read(fullPath);
            var artists = meta.Artists.Count > 0 ? meta.Artists : meta.AlbumArtists;
            return new QueueItem(
                Id: NewId(),
                Path: fullPath,
                Title: string.IsNullOrWhiteSpace(meta.Title) ? FileNameTitle(fullPath) : meta.Title,
                Artist: artists.Count > 0 ? string.Join(", ", artists) : null,
                Album: meta.Album,
                Duration: meta.Duration,
                Format: FormatLabel(fullPath),
                SampleRate: meta.SampleRate,
                ChannelCount: meta.ChannelCount);
        }
        catch (Exception ex) when (ex is AudioException or IOException or UnauthorizedAccessException)
        {
            _onReadFailure?.Invoke(fullPath, ex);
            return CreateFallbackItem(fullPath);
        }
    }

    /// <summary>Creates an item whose fields come only from the file name.</summary>
    public static QueueItem CreateFallbackItem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new QueueItem(NewId(), path, FileNameTitle(path), Format: FormatLabel(path));
    }

    /// <summary>
    /// Reads embedded artwork and returns a <c>data:</c> URL, preferring a front cover. Returns
    /// <see langword="null"/> when there is no usable image, it exceeds <see cref="MaxArtworkBytesForUi"/>, or the
    /// file cannot be read.
    /// </summary>
    public string? ReadArtworkDataUrl(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var options = new AudioMetadataReadOptions
            {
                MaxArtworkBytes = MaxArtworkBytesForUi,
                MaxArtworkCount = MaxArtworkCandidates,
            };
            var artwork = _reader.ReadArtwork(Path.GetFullPath(path), options);
            var chosen = artwork.FirstOrDefault(a => a.Kind == AudioArtworkKind.FrontCover && !a.Data.IsEmpty)
                ?? artwork.FirstOrDefault(a => !a.Data.IsEmpty);
            return chosen is null ? null : BuildDataUrl(chosen.Data.Span);
        }
        catch (Exception ex) when (ex is AudioException or IOException or UnauthorizedAccessException)
        {
            _onReadFailure?.Invoke(path, ex);
            return null;
        }
    }

    /// <summary>
    /// Builds a <c>data:</c> URL from encoded image bytes. The MIME type is determined by sniffing the bytes
    /// (tag-supplied MIME strings are not trusted). Returns <see langword="null"/> for empty, oversized, or
    /// unrecognized data.
    /// </summary>
    public static string? BuildDataUrl(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > MaxArtworkBytesForUi)
        {
            return null;
        }

        var mime = SniffImageMime(data);
        return mime is null ? null : $"data:{mime};base64,{Convert.ToBase64String(data)}";
    }

    /// <summary>Title derived from the file name without extension (application-side display fallback).</summary>
    public static string FileNameTitle(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string? FormatLabel(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return ext.Length == 0 ? null : ext.ToUpperInvariant();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string? SniffImageMime(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
        {
            return "image/png";
        }

        if (d.Length >= 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'8')
        {
            return "image/gif";
        }

        if (d.Length >= 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F'
            && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (d.Length >= 2 && d[0] == (byte)'B' && d[1] == (byte)'M')
        {
            return "image/bmp";
        }

        return null;
    }
}
