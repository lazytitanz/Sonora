namespace Sonora.Audio.Playback;

/// <summary>
/// Arguments for <see cref="AudioPlayer.TrackTransitioned"/>.
/// Raised after a gapless consumption-boundary handoff; delivery is deferred off the audio callback
/// and may occur after samples of the new track have already entered the output pipeline.
/// </summary>
public sealed class TrackTransitionedEventArgs : EventArgs
{
    /// <summary>Creates transition event args.</summary>
    public TrackTransitionedEventArgs(
        string? previousPath,
        string? nextPath,
        TimeSpan previousDuration,
        int transitionId)
    {
        PreviousPath = previousPath;
        NextPath = nextPath;
        PreviousDuration = previousDuration;
        TransitionId = transitionId;
    }

    /// <summary>Path of the track that just finished at the consumption boundary.</summary>
    public string? PreviousPath { get; }

    /// <summary>Path of the track that is now active.</summary>
    public string? NextPath { get; }

    /// <summary>Duration of the previous track when known.</summary>
    public TimeSpan PreviousDuration { get; }

    /// <summary>Monotonic id used to discard stale deferred notifications.</summary>
    public int TransitionId { get; }
}
