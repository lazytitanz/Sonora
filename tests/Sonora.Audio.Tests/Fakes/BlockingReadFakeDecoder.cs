using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Tests.Fakes;

/// <summary>
/// Fake decoder whose <see cref="Read"/> blocks until <see cref="ReleaseRead"/> — for ownership tests.
/// </summary>
internal sealed class BlockingReadFakeDecoder : IAudioDecoder
{
    private readonly FakeAudioDecoder _inner;
    private readonly ManualResetEventSlim _readEntered = new(false);
    private readonly ManualResetEventSlim _readContinue = new(false);
    private int _blockReads = 1;
    private int _disposeCount;
    private int _seekCount;

    public BlockingReadFakeDecoder(FakeAudioDecoder inner, int blockReadCount = 1)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _blockReads = Math.Max(1, blockReadCount);
    }

    public AudioFormat Format => _inner.Format;
    public TimeSpan Duration => _inner.Duration;
    public bool HasKnownDuration => _inner.HasKnownDuration;
    public bool CanSeek => _inner.CanSeek;
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int SeekCount => Volatile.Read(ref _seekCount);
    public bool IsDisposed => DisposeCount > 0;

    public bool WaitUntilReadEntered(TimeSpan timeout) => _readEntered.Wait(timeout);

    public void ReleaseRead() => _readContinue.Set();

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (Interlocked.Decrement(ref _blockReads) >= 0)
        {
            _readEntered.Set();
            _readContinue.Wait();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
        }

        return _inner.Read(destination);
    }

    public AudioSeekResult Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Interlocked.Increment(ref _seekCount);
        return _inner.Seek(position);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        ReleaseRead();
        _inner.Dispose();
        _readEntered.Dispose();
        _readContinue.Dispose();
    }
}
