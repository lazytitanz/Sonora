using Sonora.Audio.Devices;
using Sonora.Audio.Formats;
using Sonora.Audio.Network;

namespace Sonora.Audio.Diagnostics;

/// <summary>
/// Lightweight diagnostics snapshot for a player. Fields are best-effort atomics / volatiles
/// and must remain safe to read without holding the player command gate.
/// </summary>
public sealed class PlaybackDiagnostics
{
    /// <summary>Number of underrun episodes observed since the last load.</summary>
    public long UnderrunCount { get; init; }

    /// <summary>Configured ring-buffer capacity in interleaved samples.</summary>
    public int BufferCapacitySamples { get; init; }

    /// <summary>Current buffered interleaved sample count.</summary>
    public int BufferedSamples { get; init; }

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

    /// <summary>Session generation.</summary>
    public int SessionGeneration { get; init; }

    /// <summary>Active track generation.</summary>
    public int ActiveTrackGeneration { get; init; }

    /// <summary>Last gapless transition id.</summary>
    public int TransitionId { get; init; }

    /// <summary>Whether natural EOS completion is currently accepted.</summary>
    public bool AcceptEndOfStreamCompletion { get; init; }

    /// <summary>Current playback state.</summary>
    public PlaybackState State { get; init; }

    /// <summary>Output device status.</summary>
    public OutputDeviceStatus OutputDeviceStatus { get; init; }

    /// <summary>Next-track slot state.</summary>
    public NextTrackState NextTrackState { get; init; }

    /// <summary>Network transport status.</summary>
    public NetworkTransportStatus NetworkStatus { get; init; }

    /// <summary>Last silence reason observed on the pull path.</summary>
    public SeekSilenceReason LastSilenceReason { get; init; }

    /// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> of the last output Read.</summary>
    public long LastCallbackTimestamp { get; init; }

    /// <summary>Count of output Read invocations (wraps).</summary>
    public long CallbackCount { get; init; }

    /// <summary>Last seek id that began.</summary>
    public int LastSeekId { get; init; }

    /// <summary>Last seek id that reached a terminal stage.</summary>
    public int LastSeekTerminalId { get; init; }

    /// <summary>Last gapless promote transition id.</summary>
    public int LastPromoteTransitionId { get; init; }

    /// <summary>How many times StopPump waited out its join timeout.</summary>
    public long StopPumpTimeoutCount { get; init; }

    /// <summary>
    /// True when a StopPump timeout left a decode worker that may still touch the decoder.
    /// </summary>
    public bool OrphanDecodeWorker { get; init; }

    /// <summary>Trace ring dropped (torn/skipped) event count.</summary>
    public long TraceDroppedCount { get; init; }

    /// <summary>Trace ring overwritten slot count.</summary>
    public long TraceOverwrittenCount { get; init; }

    /// <summary>Active pipeline identity hash, or 0.</summary>
    public int ActivePipelineId { get; init; }

    /// <summary>Current source path/identifier when known.</summary>
    public string? SourcePath { get; init; }
}
