namespace Sonora.Audio.Network;

/// <summary>Bounded single-producer / single-consumer byte ring used by HTTP transport.</summary>
internal sealed class BoundedByteRing
{
    private readonly byte[] _buffer;
    private readonly object _sync = new();
    private int _read;
    private int _write;
    private int _count;
    private bool _completed;
    private Exception? _failure;
    private bool _disposed;

    public BoundedByteRing(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buffer = new byte[capacity];
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
                return _completed && _count == 0 && _failure is null;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> source, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int written;
            lock (_sync)
            {
                ThrowIfDisposed_NoLock();
                if (_failure is not null)
                {
                    throw _failure;
                }

                while (_count == _buffer.Length && !_disposed && _failure is null)
                {
                    Monitor.Wait(_sync, 50);
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfDisposed_NoLock();
                }

                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(BoundedByteRing));
                }

                if (_failure is not null)
                {
                    throw _failure;
                }

                var space = _buffer.Length - _count;
                written = Math.Min(space, source.Length - offset);
                for (var i = 0; i < written; i++)
                {
                    _buffer[_write] = source[offset + i];
                    _write = (_write + 1) % _buffer.Length;
                }

                _count += written;
                Monitor.PulseAll(_sync);
            }

            offset += written;
        }
    }

    /// <summary>
    /// Blocks until at least one byte is available, the stream completes, fails, is cancelled, or disposed.
    /// Returns 0 only when completed with no remaining bytes (genuine EOF).
    /// </summary>
    public int Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_sync)
        {
            while (_count == 0 && !_completed && _failure is null && !_disposed)
            {
                Monitor.Wait(_sync, 50);
                cancellationToken.ThrowIfCancellationRequested();
            }

            ThrowIfDisposed_NoLock();
            if (_failure is not null)
            {
                throw _failure;
            }

            if (_count == 0)
            {
                return 0; // completed
            }

            var toRead = Math.Min(_count, destination.Length);
            for (var i = 0; i < toRead; i++)
            {
                destination[i] = _buffer[_read];
                _read = (_read + 1) % _buffer.Length;
            }

            _count -= toRead;
            Monitor.PulseAll(_sync);
            return toRead;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _read = 0;
            _write = 0;
            _count = 0;
            _completed = false;
            _failure = null;
            Monitor.PulseAll(_sync);
        }
    }

    public void MarkCompleted()
    {
        lock (_sync)
        {
            _completed = true;
            Monitor.PulseAll(_sync);
        }
    }

    public void MarkFailed(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_sync)
        {
            _failure ??= error;
            Monitor.PulseAll(_sync);
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

    private void ThrowIfDisposed_NoLock()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BoundedByteRing));
        }
    }
}
