namespace Sonora.Audio.Metadata;

/// <summary>
/// Classification of embedded artwork when the container reports a picture type.
/// </summary>
public enum AudioArtworkKind
{
    /// <summary>Unspecified or unrecognized picture type.</summary>
    Other = 0,

    /// <summary>Front cover.</summary>
    FrontCover = 1,

    /// <summary>Back cover.</summary>
    BackCover = 2,

    /// <summary>Artist or performer image.</summary>
    Artist = 3,

    /// <summary>Leaflet or booklet page.</summary>
    Leaflet = 4,

    /// <summary>Media label (for example CD face).</summary>
    Media = 5,
}
