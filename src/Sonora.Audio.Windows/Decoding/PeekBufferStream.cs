namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Buffers the first N bytes of a possibly non-seekable stream so format sniffing can rewind.
/// Prefetched bytes are removed from the inner stream's unread sequence; logical position 0
/// is the start of the prefetched prefix.
/// </summary>
internal sealed class PeekBufferStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly byte[] _prefix;
    private readonly int _prefixLength;
    /// <summary>Inner absolute position that corresponds to logical position 0 (seekable only).</summary>
    private readonly long _innerOrigin;
    private long _position;
    private bool _disposed;

    private PeekBufferStream(
        Stream inner,
        bool leaveOpen,
        byte[] prefix,
        int prefixLength,
        long innerOrigin)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
        _prefix = prefix;
        _prefixLength = prefixLength;
        _innerOrigin = innerOrigin;
    }

    /// <summary>Prefetched header bytes available for sniffing without mutating position.</summary>
    public ReadOnlySpan<byte> PrefetchSpan => _prefix.AsSpan(0, _prefixLength);

    public static PeekBufferStream Create(Stream inner, int peekBytes, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(peekBytes);
        if (!inner.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(inner));
        }

        long innerOrigin = 0;
        if (inner.CanSeek)
        {
            innerOrigin = inner.Position;
        }

        var prefix = new byte[peekBytes];
        var total = 0;
        while (total < peekBytes)
        {
            var read = inner.Read(prefix, total, peekBytes - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return new PeekBufferStream(inner, leaveOpen, prefix, total, innerOrigin);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed && _inner.CanSeek;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inner.CanSeek)
            {
                throw new NotSupportedException("The inner stream does not report a length.");
            }

            return _inner.Length - _innerOrigin;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var written = 0;
        if (_position < _prefixLength)
        {
            var fromPrefix = (int)Math.Min(buffer.Length, _prefixLength - _position);
            _prefix.AsSpan((int)_position, fromPrefix).CopyTo(buffer);
            _position += fromPrefix;
            written += fromPrefix;
            if (written == buffer.Length)
            {
                return written;
            }
        }

        if (_inner.CanSeek)
        {
            var expectedInner = _innerOrigin + _position;
            if (_inner.Position != expectedInner)
            {
                _inner.Position = expectedInner;
            }
        }

        var read = _inner.Read(buffer[written..]);
        if (read > 0)
        {
            _position += read;
            written += read;
        }

        return written;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        // Always allow rewinding within the prefetch window, even when the inner stream cannot seek.
        if (target <= _prefixLength)
        {
            _position = target;
            return _position;
        }

        if (!_inner.CanSeek)
        {
            throw new NotSupportedException(
                "Cannot seek past the prefetched header on a non-seekable stream.");
        }

        _inner.Position = _innerOrigin + target;
        _position = target;
        return _position;
    }

    /// <summary>
    /// Rewinds to the start of the prefetched prefix without requiring <see cref="CanSeek"/>.
    /// </summary>
    public void RewindToStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _position = 0;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }
}
