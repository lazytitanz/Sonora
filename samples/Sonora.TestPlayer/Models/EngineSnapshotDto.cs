using Sonora.Audio.Formats;
using Sonora.Audio.Network;

namespace Sonora.TestPlayer.Models;

/// <summary>
/// Point-in-time view of the player and the application-owned queue. Properties are plain values so the
/// bridge can serialize it directly (camelCase, enums as camelCase strings).
/// </summary>
public sealed record EngineSnapshotDto
{
    /// <summary>UTC time the snapshot was captured.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Whether the controller has been disposed (all other values are defaults when true).</summary>
    public bool Disposed { get; init; }

    /// <summary>Whether an engine and player exist.</summary>
    public bool EngineCreated { get; init; }

    /// <summary>Transport state.</summary>
    public PlaybackState State { get; init; }

    /// <summary>Whether a source is loaded.</summary>
    public bool HasSource { get; init; }

    /// <summary>Loaded source identifier (path or URI), or <see langword="null"/>.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Id of the queue entry (or stream item) currently loaded, or <see langword="null"/>.</summary>
    public string? CurrentItemId { get; init; }

    /// <summary>Index of the current item in the queue, or -1 when it is not queued (stream / removed).</summary>
    public int QueueIndex { get; init; } = -1;

    /// <summary>Number of queued items.</summary>
    public int QueueLength { get; init; }

    /// <summary>Estimated audible position in milliseconds (an estimate, not sample-accurate).</summary>
    public double PositionMs { get; init; }

    /// <summary>Decode frontier in milliseconds.</summary>
    public double BufferedPositionMs { get; init; }

    /// <summary>Duration in milliseconds (0 when unknown).</summary>
    public double DurationMs { get; init; }

    /// <summary>Whether <see cref="DurationMs"/> is known.</summary>
    public bool HasKnownDuration { get; init; }

    /// <summary>Linear volume 0–1.</summary>
    public float Volume { get; init; }

    /// <summary>Whether the controller preloads the next queue item for gapless playback.</summary>
    public bool GaplessEnabled { get; init; }

    /// <summary>Path of the preloaded next track, or <see langword="null"/>.</summary>
    public string? NextTrackPath { get; init; }

    /// <summary>State of the single preload slot.</summary>
    public NextTrackState NextTrackState { get; init; }

    /// <summary>Network transport status (idle for local files).</summary>
    public NetworkTransportStatus NetworkStatus { get; init; }

    /// <summary>Bytes buffered by the network transport.</summary>
    public int NetworkBytesBuffered { get; init; }

    /// <summary>Underrun episodes since the last load (engine counter).</summary>
    public long UnderrunCount { get; init; }

    /// <summary>Buffered interleaved samples in the active ring.</summary>
    public int BufferedSamples { get; init; }

    /// <summary>Ring capacity in interleaved samples.</summary>
    public int BufferCapacitySamples { get; init; }

    /// <summary>Output configuration and effective state.</summary>
    public OutputConfigDto? Output { get; init; }

    /// <summary>Queue session generation; increments on every load/stop and invalidates stale events.</summary>
    public int Generation { get; init; }

    /// <summary><c>TrackTransitioned</c> events accepted by the queue coordinator since startup.</summary>
    public int TransitionsObserved { get; init; }

    /// <summary><c>PlaybackCompleted</c> events observed (including ignored ones) since startup.</summary>
    public int CompletionsObserved { get; init; }

    /// <summary>Queue advances performed via <c>PlaybackCompleted</c> (non-gapless fallback path).</summary>
    public int CompletionAdvances { get; init; }

    /// <summary><c>ErrorOccurred</c> events since startup.</summary>
    public int ErrorsObserved { get; init; }

    /// <summary><c>Underrun</c> events since startup.</summary>
    public int UnderrunEventsObserved { get; init; }
}
