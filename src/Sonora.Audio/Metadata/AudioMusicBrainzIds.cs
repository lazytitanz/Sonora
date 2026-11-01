namespace Sonora.Audio.Metadata;

/// <summary>
/// MusicBrainz identifiers when present in tags.
/// </summary>
/// <param name="TrackId">Recording / track MBID.</param>
/// <param name="ReleaseId">Release MBID.</param>
/// <param name="ArtistId">Artist MBID.</param>
/// <param name="ReleaseGroupId">Release group MBID.</param>
public sealed record AudioMusicBrainzIds(
    string? TrackId,
    string? ReleaseId,
    string? ArtistId,
    string? ReleaseGroupId);
