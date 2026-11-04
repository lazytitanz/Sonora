using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Returns a short interleaved PCM prefix (e.g. open-time probe leftovers) before delegating to an inner decoder.
/// </summary>
internal sealed class PrefixedAudioDecoder : IAudioDecoder
{
    private readonly IAudioDecoder _inner;
    private float[]? _prefix;
    private int _prefixOffset;
    private bool _disposed;

    public PrefixedAudioDecoder(IAudioDecoder inner, float[] prefix, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, prefix.Length);

        _inner = inner;
        if (prefixLength > 0)
        {
            _prefix = new float[prefixLength];
            prefix.AsSpan(0, prefixLength).CopyTo(_prefix);
        }
    }

    public AudioFormat Format => _inner.Format;
    public TimeSpan Duration => _inner.Duration;
    public bool HasKnownDuration => _inner.HasKnownDuration;
    public bool CanSeek => _inner.CanSeek;

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.IsEmpty)
        {
            return 0;
        }

        var written = 0;
        if (_prefix is not null && _prefixOffset < _prefix.Length)
        {
            var available = _prefix.Length - _prefixOffset;
            var toCopy = Math.Min(available, destination.Length);
            _prefix.AsSpan(_prefixOffset, toCopy).CopyTo(destination[..toCopy]);
            _prefixOffset += toCopy;
            written += toCopy;
            if (_prefixOffset >= _prefix.Length)
            {
                _prefix = null;
            }

            if (written >= destination.Length)
            {
                return written;
            }
        }

        written += _inner.Read(destination[written..]);
        return written;
    }

    public AudioSeekResult Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _prefix = null;
        _prefixOffset = 0;
        return _inner.Seek(position);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _prefix = null;
        _inner.Dispose();
    }
}
