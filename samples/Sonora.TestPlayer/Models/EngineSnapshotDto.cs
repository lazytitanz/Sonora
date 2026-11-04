using Sonora.Audio.Diagnostics;
using Sonora.Audio.Devices;
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

    /// <summary>Configured crossfade overlap in milliseconds (<c>0</c> = hard-cut gapless).</summary>
    public double CrossfadeDurationMs { get; init; }

    /// <summary>Crossfade gain curve (<c>linear</c> or <c>equalPower</c>).</summary>
    public string CrossfadeCurve { get; init; } = "equalPower";

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

    /// <summary>Samples written into the ring since the last timeline rebase.</summary>
    public long SamplesDecoded { get; init; }

    /// <summary>Samples consumed from the ring since the last timeline rebase.</summary>
    public long SamplesConsumed { get; init; }

    /// <summary>Nested in-flight seek count.</summary>
    public int SeekInProgress { get; init; }

    /// <summary>Effects barrier armed (1) or clear (0).</summary>
    public int EffectsBarrier { get; init; }

    /// <summary>Whether the active decode pump reports running.</summary>
    public bool PumpRunning { get; init; }

    /// <summary>Whether the active ring is completed and drained.</summary>
    public bool RingCompleted { get; init; }

    /// <summary>Last silence reason on the pull path.</summary>
    public SeekSilenceReason LastSilenceReason { get; init; }

    /// <summary>Output Read heartbeat timestamp (<see cref="System.Diagnostics.Stopwatch"/> ticks).</summary>
    public long LastCallbackTimestamp { get; init; }

    /// <summary>Output Read count.</summary>
    public long CallbackCount { get; init; }

    /// <summary>Last seek id that began.</summary>
    public int LastSeekId { get; init; }

    /// <summary>Last seek id that reached a terminal stage.</summary>
    public int LastSeekTerminalId { get; init; }

    /// <summary>Last gapless promote transition id.</summary>
    public int LastPromoteTransitionId { get; init; }

    /// <summary>StopPump join timeouts.</summary>
    public long StopPumpTimeoutCount { get; init; }

    /// <summary>StopPump timed out and decode worker may still touch the decoder.</summary>
    public bool OrphanDecodeWorker { get; init; }

    /// <summary>Seek-trace ring dropped events.</summary>
    public long TraceDroppedCount { get; init; }

    /// <summary>Seek-trace ring overwritten slots.</summary>
    public long TraceOverwrittenCount { get; init; }

    /// <summary>Output device status.</summary>
    public OutputDeviceStatus OutputDeviceStatus { get; init; }

    /// <summary>Active pipeline identity hash.</summary>
    public int ActivePipelineId { get; init; }
}
