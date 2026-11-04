using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Owns a byte <see cref="Stream"/> that an inner decoder reads without taking ownership of it.
/// </summary>
internal sealed class OwningStreamAudioDecoder : IAudioDecoder
{
    private readonly IAudioDecoder _inner;
    private readonly Stream _stream;
    private bool _disposed;

    public OwningStreamAudioDecoder(IAudioDecoder inner, Stream stream)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public AudioFormat Format => _inner.Format;
    public TimeSpan Duration => _inner.Duration;
    public bool HasKnownDuration => _inner.HasKnownDuration;
    public bool CanSeek => _inner.CanSeek;

    public int Read(Span<float> destination) => _inner.Read(destination);

    public AudioSeekResult Seek(TimeSpan position) => _inner.Seek(position);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
        _stream.Dispose();
    }
}
