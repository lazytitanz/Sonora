namespace Sonora.Audio.Diagnostics;

/// <summary>
/// Bounded multi-producer overwrite ring for seek/playback lifecycle events.
/// Safe for the audio callback, seek command threads, and decode workers.
/// Allocation-free on the write path. Readers copy under a best-effort scan and
/// may observe torn slots (skipped via sequence validation).
/// </summary>
public sealed class SeekLifecycleTraceRing
{
    private const int DefaultCapacity = 2048;

    private readonly SeekLifecycleEvent[] _slots;
    private readonly int _mask;
    private long _writeSequence;
    private long _dropped;
    private long _overwritten;

    /// <summary>Creates a power-of-two capacity ring (default 2048).</summary>
    public SeekLifecycleTraceRing(int capacity = DefaultCapacity)
    {
        if (capacity < 16 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be a power of two >= 16.");
        }

        _slots = new SeekLifecycleEvent[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Number of slots.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Events that could not be retained because the ring wrapped before a drain.</summary>
    public long DroppedCount => Volatile.Read(ref _dropped);

    /// <summary>Slots overwritten while a prior event was still unread (approximate).</summary>
    public long OverwrittenCount => Volatile.Read(ref _overwritten);

    /// <summary>Total events published (including those later overwritten).</summary>
    public long PublishedCount => Volatile.Read(ref _writeSequence);

    /// <summary>
    /// Records an event. Allocation-free; safe from any thread including the audio callback.
    /// </summary>
    public void Record(ref SeekLifecycleEvent evt)
    {
        var seq = Interlocked.Increment(ref _writeSequence);
        var index = (int)((seq - 1) & _mask);

        // Seqlock-style: odd sequence while writing, even/final sequence publishes.
        // Readers that see mismatched head/tail sequence skip the slot.
        Volatile.Write(ref _slots[index].Sequence, 0);

        evt.Sequence = seq;
        if (evt.Timestamp == 0)
        {
            evt.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        _slots[index].Timestamp = evt.Timestamp;
        _slots[index].SeekId = evt.SeekId;
        _slots[index].SessionGeneration = evt.SessionGeneration;
        _slots[index].TrackGeneration = evt.TrackGeneration;
        _slots[index].PipelineId = evt.PipelineId;
        _slots[index].Stage = evt.Stage;
        _slots[index].SilenceReason = evt.SilenceReason;
        _slots[index].RequestedTicks = evt.RequestedTicks;
        _slots[index].ActualTicks = evt.ActualTicks;
        _slots[index].Accuracy = evt.Accuracy;
        _slots[index].Outcome = evt.Outcome;
        _slots[index].RingCount = evt.RingCount;
        _slots[index].SamplesDecoded = evt.SamplesDecoded;
        _slots[index].SamplesConsumed = evt.SamplesConsumed;
        _slots[index].Flags = evt.Flags;

        Volatile.Write(ref _slots[index].Sequence, seq);

        // When more than Capacity events have been published without a full drain,
        // older slots are overwritten. Count each wrap step once per publish past capacity.
        if (seq > _slots.Length)
        {
            Interlocked.Increment(ref _overwritten);
        }
    }

    /// <summary>Convenience overload that copies the struct then records.</summary>
    public void Record(SeekLifecycleEvent evt) => Record(ref evt);

    /// <summary>
    /// Copies recent events into <paramref name="destination"/> in chronological order (oldest first
    /// among retained). Returns the count written. Best-effort; does not take player locks.
    /// </summary>
    public int CopyRecent(Span<SeekLifecycleEvent> destination, out long droppedSnapshot, out long overwrittenSnapshot)
    {
        droppedSnapshot = Volatile.Read(ref _dropped);
        overwrittenSnapshot = Volatile.Read(ref _overwritten);

        if (destination.IsEmpty)
        {
            return 0;
        }

        var published = Volatile.Read(ref _writeSequence);
        if (published <= 0)
        {
            return 0;
        }

        var capacity = _slots.Length;
        var available = (int)Math.Min(published, capacity);
        var toCopy = Math.Min(available, destination.Length);
        var startSeq = published - toCopy + 1;
        var written = 0;

        for (var seq = startSeq; seq <= published && written < destination.Length; seq++)
        {
            var index = (int)((seq - 1) & _mask);
            // Validate slot was published for this sequence (skip torn / overwritten mismatches).
            var head = Volatile.Read(ref _slots[index].Sequence);
            if (head != seq)
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }

            var copy = _slots[index];
            var tail = Volatile.Read(ref _slots[index].Sequence);
            if (tail != seq || copy.Sequence != seq)
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }

            destination[written++] = copy;
        }

        droppedSnapshot = Volatile.Read(ref _dropped);
        overwrittenSnapshot = Volatile.Read(ref _overwritten);
        return written;
    }

    /// <summary>Clears counters and slots (test / dispose helper; not audio-thread critical).</summary>
    public void Reset()
    {
        Array.Clear(_slots);
        Volatile.Write(ref _writeSequence, 0);
        Volatile.Write(ref _dropped, 0);
        Volatile.Write(ref _overwritten, 0);
    }
}
