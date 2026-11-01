namespace Sonora.Audio.Metadata;

/// <summary>
/// Options for reading track metadata. Defaults favor fast library scans:
/// artwork bytes are not materialized unless <see cref="IncludeArtwork"/> is <see langword="true"/>.
/// </summary>
public sealed class AudioMetadataReadOptions
{
    /// <summary>
    /// When <see langword="true"/>, embedded artwork bytes are copied into <see cref="AudioTrackMetadata.Artwork"/>.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool IncludeArtwork { get; init; }

    /// <summary>
    /// Maximum encoded size in bytes for a single artwork image. Larger images are omitted.
    /// Default: 20 MiB.
    /// </summary>
    public int MaxArtworkBytes
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 20 * 1024 * 1024;

    /// <summary>
    /// Maximum number of artwork images to return. Additional images are omitted.
    /// Default: 16.
    /// </summary>
    public int MaxArtworkCount
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 16;
}
