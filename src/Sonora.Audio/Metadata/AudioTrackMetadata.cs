namespace Sonora.Audio.Metadata;

/// <summary>
/// Immutable, platform-neutral track metadata.
/// Unavailable fields are <see langword="null"/> (or empty collections).
/// Sonora never invents a title from the file name.
/// </summary>
public sealed record AudioTrackMetadata
{
    private static readonly IReadOnlyList<string> EmptyStrings = Array.Empty<string>();
    private static readonly IReadOnlyList<AudioArtwork> EmptyArtwork = Array.Empty<AudioArtwork>();
    private static readonly IReadOnlyDictionary<string, string> EmptyTags =
        new Dictionary<string, string>(0);

    /// <summary>Track title, or <see langword="null"/> when absent.</summary>
    public string? Title { get; init; }

    /// <summary>Track artists. Empty when none are available. Never <see langword="null"/>.</summary>
    public IReadOnlyList<string> Artists { get; init; } = EmptyStrings;

    /// <summary>Album title, or <see langword="null"/> when absent.</summary>
    public string? Album { get; init; }

    /// <summary>Album artists. Empty when none are available.</summary>
    public IReadOnlyList<string> AlbumArtists { get; init; } = EmptyStrings;

    /// <summary>Primary genre (typically the first of <see cref="Genres"/>), or <see langword="null"/>.</summary>
    public string? Genre { get; init; }

    /// <summary>Genres. Empty when none are available.</summary>
    public IReadOnlyList<string> Genres { get; init; } = EmptyStrings;

    /// <summary>Track number within the album/disc, or <see langword="null"/>.</summary>
    public int? TrackNumber { get; init; }

    /// <summary>Total tracks on the disc/album, or <see langword="null"/>.</summary>
    public int? TrackCount { get; init; }

    /// <summary>Disc number, or <see langword="null"/>.</summary>
    public int? DiscNumber { get; init; }

    /// <summary>Total discs, or <see langword="null"/>.</summary>
    public int? DiscCount { get; init; }

    /// <summary>Recording year, or <see langword="null"/>.</summary>
    public int? Year { get; init; }

    /// <summary>Original release date when available as a calendar date.</summary>
    public DateOnly? OriginalReleaseDate { get; init; }

    /// <summary>
    /// Container-reported duration when available.
    /// Not guaranteed sample-accurate for formats with encoder delay or padding.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Composer, or <see langword="null"/>.</summary>
    public string? Composer { get; init; }

    /// <summary>Comment, or <see langword="null"/>.</summary>
    public string? Comment { get; init; }

    /// <summary>Copyright notice, or <see langword="null"/>.</summary>
    public string? Copyright { get; init; }

    /// <summary>Publisher, or <see langword="null"/>.</summary>
    public string? Publisher { get; init; }

    /// <summary>Encoder or encoded-by information, or <see langword="null"/>.</summary>
    public string? Encoder { get; init; }

    /// <summary>Unsynchronized plain-text lyrics when embedded; never timed/synced lyrics.</summary>
    public string? Lyrics { get; init; }

    /// <summary>Beats per minute, or <see langword="null"/>.</summary>
    public double? BeatsPerMinute { get; init; }

    /// <summary>Compilation flag when the tag provides one.</summary>
    public bool? IsCompilation { get; init; }

    /// <summary>Approximate bitrate in bits per second, or <see langword="null"/>.</summary>
    public long? Bitrate { get; init; }

    /// <summary>Sample rate in Hz, or <see langword="null"/>.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Channel count, or <see langword="null"/>.</summary>
    public int? ChannelCount { get; init; }

    /// <summary>
    /// Whether the codec family is lossless when the reader can establish it reliably.
    /// Never inferred from the file extension alone.
    /// </summary>
    public bool? IsLossless { get; init; }

    /// <summary>ReplayGain values when present.</summary>
    public AudioReplayGain? ReplayGain { get; init; }

    /// <summary>MusicBrainz identifiers when present.</summary>
    public AudioMusicBrainzIds? MusicBrainz { get; init; }

    /// <summary>
    /// Embedded artwork. Empty unless artwork was requested.
    /// Prefer <see cref="AudioArtworkKind.FrontCover"/> when selecting a cover.
    /// Ordering preserves container order with front covers sorted ahead of other kinds for convenience.
    /// </summary>
    public IReadOnlyList<AudioArtwork> Artwork { get; init; } = EmptyArtwork;

    /// <summary>
    /// Additional tag key/value pairs not mapped to normalized fields.
    /// Duplicate keys: last value wins. Values may be truncated to resource limits.
    /// </summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } = EmptyTags;
}
