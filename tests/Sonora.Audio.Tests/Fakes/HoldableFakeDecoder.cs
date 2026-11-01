using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Tests.Fakes;

/// <summary>
/// Wraps a decoder and, until <see cref="Release"/>, returns non-empty silence instead of
/// advancing the inner decoder. Keeps playback away from EOF without blocking the decode pump
/// or depending on wall-clock duration.
/// </summary>
internal sealed class HoldableFakeDecoder : IAudioDecoder
{
    private readonly FakeAudioDecoder _inner;
    private int _released;

    public HoldableFakeDecoder(FakeAudioDecoder inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public AudioFormat Format => _inner.Format;
    public TimeSpan Duration => _inner.Duration;
    public bool CanSeek => _inner.CanSeek;

    /// <summary>Allows subsequent reads to consume the inner decoder (and eventually EOF).</summary>
    public void Release() => Volatile.Write(ref _released, 1);

    public int Read(Span<float> destination)
    {
        if (Volatile.Read(ref _released) == 0)
        {
            // Non-empty silence: output/pump stay active but never reach end-of-stream.
            destination.Clear();
            return destination.Length;
        }

        return _inner.Read(destination);
    }

    public AudioSeekResult Seek(TimeSpan position) => _inner.Seek(position);

    public void Dispose()
    {
        Release();
        _inner.Dispose();
    }
}
