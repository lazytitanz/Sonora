namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Preallocated ring of per-period telemetry for
/// <see cref="WasapiRenderSubmissionProbe"/> (no callback allocations).
/// </summary>
internal sealed class RenderSubmissionPeriodJournal
{
    private readonly RenderSubmissionPeriodRecord[] _records;
    private int _write;
    private int _count;
    private long _sequence;

    public RenderSubmissionPeriodJournal(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _records = new RenderSubmissionPeriodRecord[capacity];
    }

    public int Capacity => _records.Length;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Callback-safe: single writer (WASAPI fill thread).</summary>
    public void Record(int bytesRequested, int bytesReturned, int blockAlign, bool oddInterleavedFloats)
    {
        var framesRequested = blockAlign > 0 ? bytesRequested / blockAlign : 0;
        var framesReturned = blockAlign > 0 ? bytesReturned / blockAlign : 0;
        var shortRead = bytesReturned > 0 && bytesReturned < bytesRequested;
        var silentOrEmpty = bytesReturned == 0;
        var seq = Interlocked.Increment(ref _sequence);

        var idx = Volatile.Read(ref _write);
        _records[idx] = new RenderSubmissionPeriodRecord(
            Sequence: seq,
            BytesRequested: bytesRequested,
            BytesReturned: bytesReturned,
            FramesRequested: framesRequested,
            FramesReturned: framesReturned,
            BlockAlign: blockAlign,
            ShortRead: shortRead,
            EmptyRead: silentOrEmpty,
            OddInterleavedFloatCount: oddInterleavedFloats);

        var next = idx + 1;
        if (next >= Capacity)
        {
            next = 0;
        }

        Volatile.Write(ref _write, next);
        var count = Volatile.Read(ref _count);
        if (count < Capacity)
        {
            Volatile.Write(ref _count, count + 1);
        }
    }

    public void Clear()
    {
        Volatile.Write(ref _write, 0);
        Volatile.Write(ref _count, 0);
        Interlocked.Exchange(ref _sequence, 0);
    }

    /// <summary>Snapshot for diagnostics (call off the callback thread).</summary>
    public RenderSubmissionPeriodRecord[] Snapshot()
    {
        var count = Math.Min(Volatile.Read(ref _count), Capacity);
        if (count == 0)
        {
            return Array.Empty<RenderSubmissionPeriodRecord>();
        }

        var write = Volatile.Read(ref _write);
        var result = new RenderSubmissionPeriodRecord[count];
        if (count < Capacity)
        {
            Array.Copy(_records, 0, result, 0, count);
            return result;
        }

        // Ring is full: chronological order starts at write.
        var first = write;
        var n = Capacity - first;
        Array.Copy(_records, first, result, 0, n);
        if (first > 0)
        {
            Array.Copy(_records, 0, result, n, first);
        }

        return result;
    }
}

/// <summary>One FillBuffer-equivalent <c>IWaveProvider.Read</c> into the render-client lease buffer.</summary>
internal readonly record struct RenderSubmissionPeriodRecord(
    long Sequence,
    int BytesRequested,
    int BytesReturned,
    int FramesRequested,
    int FramesReturned,
    int BlockAlign,
    bool ShortRead,
    bool EmptyRead,
    bool OddInterleavedFloatCount);
