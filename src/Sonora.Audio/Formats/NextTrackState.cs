namespace Sonora.Audio.Formats;

/// <summary>
/// State of the single pending next-track preload slot on <see cref="AudioPlayer"/>.
/// </summary>
public enum NextTrackState
{
    /// <summary>No next track is loaded or requested.</summary>
    None = 0,

    /// <summary>A next track is opening/decoding into its bounded buffer.</summary>
    Loading = 1,

    /// <summary>Next track is primed and ready for a gapless handoff.</summary>
    Ready = 2,

    /// <summary>The latest preload attempt failed; current playback is unaffected.</summary>
    Failed = 3,
}
