namespace Sonora.Audio.Buffers;

/// <summary>
/// Bounded single-producer / single-consumer ring buffer of interleaved float PCM samples.
/// The producer may block when full; the consumer performs non-blocking reads suitable for audio callbacks.
/// </summary>
internal sealed class PcmRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _sync = new();
    private int _write;
    private int _read;
    private int _count;
    private bool _completed;
    private bool _disposed;

    public PcmRingBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacitySamples, 1);
        _buffer = new float[capacitySamples];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_sync)
            {
                return _completed && _count == 0;
            }
        }
    }

    public bool IsCompletedOrEmpty
    {
        get
        {
            lock (_sync)
            {
                return _completed && _count == 0;
            }
        }
    }

    /// <summary>
    /// Marks the producer side as finished (decoder EOF). Remaining samples may still be consumed.
    /// </summary>
    public void MarkCompleted()
    {
        lock (_sync)
        {
            _completed = true;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    /// Clears all buffered samples and resets completion. Must only be called when the producer is stopped.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _write = 0;
            _read = 0;
            _count = 0;
            _completed = false;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    /// Writes samples, waiting when the buffer is full until space is available or the wait is cancelled.
    /// </summary>
    /// <returns>Number of samples written. May be less than <paramref name="source"/>.Length on cancel.</returns>
    public int Write(ReadOnlySpan<float> source, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int chunk;
            lock (_sync)
            {
                ThrowIfDisposed();

                while (_count == _buffer.Length && !_disposed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Monitor.Wait(_sync, 50);
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (_disposed)
                {
                    break;
                }

                var space = _buffer.Length - _count;
                chunk = Math.Min(space, source.Length - written);
                CopyToRing(source.Slice(written, chunk), isWrite: true);
                _count += chunk;
                Monitor.PulseAll(_sync);
            }

            written += chunk;
        }

        return written;
    }

    /// <summary>
    /// Non-blocking read for output callbacks. Returns the number of samples copied.
    /// </summary>
    public int TryRead(Span<float> destination)
    {
        lock (_sync)
        {
            if (_disposed || _count == 0)
            {
                return 0;
            }

            var toRead = Math.Min(_count, destination.Length);
            CopyFromRing(destination[..toRead]);
            _count -= toRead;
            Monitor.PulseAll(_sync);
            return toRead;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            Monitor.PulseAll(_sync);
        }
    }

    private void CopyToRing(ReadOnlySpan<float> source, bool isWrite)
    {
        _ = isWrite;
        var first = Math.Min(source.Length, _buffer.Length - _write);
        source[..first].CopyTo(_buffer.AsSpan(_write, first));
        var remaining = source.Length - first;
        if (remaining > 0)
        {
            source.Slice(first, remaining).CopyTo(_buffer.AsSpan(0, remaining));
        }

        _write = (_write + source.Length) % _buffer.Length;
    }

    private void CopyFromRing(Span<float> destination)
    {
        var first = Math.Min(destination.Length, _buffer.Length - _read);
        _buffer.AsSpan(_read, first).CopyTo(destination[..first]);
        var remaining = destination.Length - first;
        if (remaining > 0)
        {
            _buffer.AsSpan(0, remaining).CopyTo(destination[first..]);
        }

        _read = (_read + destination.Length) % _buffer.Length;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PcmRingBuffer));
        }
    }
}
