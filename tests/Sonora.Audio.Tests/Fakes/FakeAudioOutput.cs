using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Tests.Fakes;

/// <summary>
/// Pulls PCM on a background thread when playing. Does not take the player command lock.
/// </summary>
internal sealed class FakeAudioOutput : IAudioOutput
{
    private readonly int _blockSamples;
    private readonly TimeSpan _latency;
    private IPcmSampleSource? _source;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _renderedSamples;
    private int _playing;
    private bool _disposed;

    public FakeAudioOutput(int blockSamples = 256, TimeSpan? latency = null)
    {
        _blockSamples = blockSamples;
        _latency = latency ?? TimeSpan.FromMilliseconds(20);
    }

    public AudioFormat? Format { get; private set; }
    public TimeSpan AverageLatency => _latency;
    public TimeSpan CurrentLatency => _playing == 1 ? _latency : _latency;
    public event EventHandler<AudioOutputStoppedEventArgs>? Stopped;
    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }

    public void Initialize(IPcmSampleSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Format = source.Format;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is null)
        {
            throw new InvalidOperationException("Not initialized.");
        }

        PlayCount++;
        if (Interlocked.Exchange(ref _playing, 1) == 1)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => Loop(token), token);
    }

    public void Pause()
    {
        Interlocked.Exchange(ref _playing, 0);
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }
    }

    public void Stop()
    {
        StopCount++;
        Interlocked.Exchange(ref _playing, 0);
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        Interlocked.Exchange(ref _renderedSamples, 0);
    }

    public TimeSpan? TryGetRenderedPosition()
    {
        if (Format is null)
        {
            return null;
        }

        return Format.Value.SamplesToTime(Interlocked.Read(ref _renderedSamples));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    private void Loop(CancellationToken token)
    {
        var source = _source!;
        var buffer = new float[_blockSamples];
        try
        {
            while (!token.IsCancellationRequested && Volatile.Read(ref _playing) == 1)
            {
                var read = source.Read(buffer);
                if (read <= 0 || source.IsEndOfStream)
                {
                    Interlocked.Exchange(ref _playing, 0);
                    Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(reachedEndOfStream: true));
                    return;
                }

                Interlocked.Add(ref _renderedSamples, read);
                Thread.Sleep(1);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _playing, 0);
            Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(reachedEndOfStream: false, exception: ex));
        }
    }
}
