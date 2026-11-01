namespace Sonora.Audio.Devices;

/// <summary>
/// Whether playback should resume when output becomes available again after device loss.
/// Independent of <see cref="Formats.PlaybackState"/>.
/// Cleared by explicit <c>Stop</c>, <c>LoadAsync</c>, and <c>Dispose</c>.
/// </summary>
public enum OutputRecoveryIntent
{
    /// <summary>Do not auto-resume when output returns.</summary>
    None = 0,

    /// <summary>Resume playing when a usable output is restored.</summary>
    ResumePlaying = 1,

    /// <summary>Remain paused (position preserved) when output is restored.</summary>
    RemainPaused = 2,
}
