namespace Sonora.Audio.Diagnostics;

/// <summary>
/// Blittable seek/playback lifecycle event for the multi-producer trace ring.
/// Sequence is written last (publish) and checked by readers to detect torn slots.
/// </summary>
public struct SeekLifecycleEvent
{
    /// <summary>Monotonic publish sequence (1-based). Zero means empty/uninitialized.</summary>
    public long Sequence;

    /// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> at record time.</summary>
    public long Timestamp;

    /// <summary>Correlated seek request id (0 when not seek-scoped).</summary>
    public int SeekId;

    /// <summary>Player session generation.</summary>
    public int SessionGeneration;

    /// <summary>Active track generation at record time.</summary>
    public int TrackGeneration;

    /// <summary>Stable pipeline identity (RuntimeHelpers.GetHashCode of pipeline instance).</summary>
    public int PipelineId;

    /// <summary>Lifecycle stage.</summary>
    public SeekLifecycleStage Stage;

    /// <summary>Silence reason when <see cref="Stage"/> is <see cref="SeekLifecycleStage.ReadSilence"/>.</summary>
    public SeekSilenceReason SilenceReason;

    /// <summary>Requested seek position in 100-ns ticks, or 0.</summary>
    public long RequestedTicks;

    /// <summary>Actual seek landing in 100-ns ticks when known; <see cref="long.MinValue"/> when unknown.</summary>
    public long ActualTicks;

    /// <summary>Cast of <see cref="Seeking.AudioSeekAccuracy"/> or -1 when N/A.</summary>
    public short Accuracy;

    /// <summary>Outcome code: 0=none, 1=ok, 2=superseded, 3=failed, 4=coalesced, 5=timeout.</summary>
    public short Outcome;

    /// <summary>Ring sample count snapshot (best-effort).</summary>
    public int RingCount;

    /// <summary>Samples decoded counter snapshot.</summary>
    public long SamplesDecoded;

    /// <summary>Samples consumed counter snapshot.</summary>
    public long SamplesConsumed;

    /// <summary>Packed flags: bit0 barrier, bit1 pumpRunning, bit2 ringCompleted, bit3 seekInProgress, bit4 nextReady, bit5 nextLoading, bit6 stopPumpTimedOut.</summary>
    public int Flags;
}
