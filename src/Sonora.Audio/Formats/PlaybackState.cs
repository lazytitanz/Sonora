namespace Sonora.Audio.Formats;

/// <summary>
/// Meaningful playback states for <see cref="AudioPlayer"/>.
/// </summary>
public enum PlaybackState
{
    /// <summary>No active playback. May or may not have a loaded source.</summary>
    Stopped = 0,

    /// <summary>Playback is actively rendering audio.</summary>
    Playing = 1,

    /// <summary>Playback is paused; position is preserved.</summary>
    Paused = 2,

    /// <summary>A source is being loaded or the buffer is priming.</summary>
    Buffering = 3,
}
