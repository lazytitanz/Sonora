namespace Sonora.Audio.Metadata;

/// <summary>
/// ReplayGain track/album gain and peak values when present in tags.
/// Gains are in decibels. Peaks are linear amplitude (typically 0–1+).
/// </summary>
/// <param name="TrackGainDb">Track gain in dB, or <see langword="null"/> when unavailable.</param>
/// <param name="TrackPeak">Track peak, or <see langword="null"/> when unavailable.</param>
/// <param name="AlbumGainDb">Album gain in dB, or <see langword="null"/> when unavailable.</param>
/// <param name="AlbumPeak">Album peak, or <see langword="null"/> when unavailable.</param>
public sealed record AudioReplayGain(
    double? TrackGainDb,
    double? TrackPeak,
    double? AlbumGainDb,
    double? AlbumPeak);
