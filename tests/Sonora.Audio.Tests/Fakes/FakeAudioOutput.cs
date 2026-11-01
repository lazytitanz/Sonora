using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Tests.Fakes;

/// <summary>
/// Pulls PCM on a background thread when playing. Does not take the player command lock.
/// </summary>
internal class FakeAudioOutput : IAudioOutput
{
    private readonly int _blockSamples;
    private readonly TimeSpan _latency;
    private readonly TimeSpan _shutdownTimeout;
    private IPcmSampleSource? _source;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _renderedSamples;
    private int _playing;
    private bool _shutdown;
    private bool _disposed;

    public FakeAudioOutput(
        int blockSamples = 256,
        TimeSpan? latency = null,
        string? endpointId = null,
        TimeSpan? shutdownTimeout = null)
    {
        _blockSamples = blockSamples;
        _latency = latency ?? TimeSpan.FromMilliseconds(20);
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        EndpointId = endpointId;
    }

    public AudioFormat? Format { get; private set; }
    public string? EndpointId { get; }
    public TimeSpan AverageLatency => _latency;
    public TimeSpan CurrentLatency => _playing == 1 ? _latency : _latency;
    public event EventHandler<AudioOutputStoppedEventArgs>? Stopped;
    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }
    public int InitializeCount { get; private set; }
    public int ShutdownCount { get; private set; }
    public int ReadCount { get; private set; }
    public int PullThreadId { get; private set; }
    public List<float> CapturedSamples { get; } = new();
    public bool IsShutdown => _shutdown;

    /// <summary>When set, <see cref="Shutdown"/> throws after attempting to stop the loop.</summary>
    public Func<Exception>? ShutdownFault { get; set; }

    public void Initialize(IPcmSampleSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Format = source.Format;
        InitializeCount++;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_shutdown)
        {
            throw new InvalidOperationException("Output has been shut down.");
        }

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
        // Do not pass token to Task.Run — Pause/Stop cancel the CTS cooperatively inside Loop
        // without marking the task cancelled before Shutdown can join it.
        _loop = Task.Run(() => Loop(token));
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

        JoinLoop(TimeSpan.FromSeconds(2));
        Interlocked.Exchange(ref _renderedSamples, 0);
    }

    public virtual void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        ShutdownCount++;

        // Sever the PCM source first so no further TransitionSampleSource reads can occur.
        Volatile.Write(ref _source, null);
        Interlocked.Exchange(ref _playing, 0);
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        OnShutdownSignaled();

        if (!JoinLoop(_shutdownTimeout))
        {
            throw new AudioOutputException(
                "Timed out waiting for the output pull loop to stop; replacement output was not started.");
        }

        var fault = ShutdownFault?.Invoke();
        if (fault is not null)
        {
            // Quiescence of the loop succeeded; surface the injected fault without retrying on Dispose.
            _shutdown = true;
            throw fault is AudioOutputException
                ? fault
                : new AudioOutputException("Output shutdown failed; replacement output was not started.", fault);
        }

        _shutdown = true;
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
        try
        {
            if (!_shutdown)
            {
                Shutdown();
            }
        }
        catch
        {
            Volatile.Write(ref _source, null);
        }

        _cts?.Dispose();
    }

    /// <summary>Simulates a device-loss / output failure stop.</summary>
    public void InjectFailure(Exception exception)
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

        Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(reachedEndOfStream: false, exception: exception));
    }

    /// <summary>Hook for holdable outputs to unblock a stuck Read during shutdown.</summary>
    protected virtual void OnShutdownSignaled()
    {
    }

    /// <summary>Optional gate around each source.Read for subclasses.</summary>
    protected virtual void BeforeRead()
    {
    }

    private bool JoinLoop(TimeSpan timeout)
    {
        var loop = _loop;
        if (loop is null || loop.IsCompleted)
        {
            return true;
        }

        try
        {
            return loop.Wait(timeout);
        }
        catch (AggregateException)
        {
            // Pull loop finished (possibly with an error); it will not read again.
            return loop.IsCompleted;
        }
        catch
        {
            return loop.IsCompleted;
        }
    }

    private void Loop(CancellationToken token)
    {
        var buffer = new float[_blockSamples];
        PullThreadId = Environment.CurrentManagedThreadId;
        try
        {
            while (!token.IsCancellationRequested && Volatile.Read(ref _playing) == 1)
            {
                BeforeRead();

                var source = Volatile.Read(ref _source);
                if (source is null)
                {
                    return;
                }

                var read = source.Read(buffer);
                ReadCount++;
                if (read > 0)
                {
                    lock (CapturedSamples)
                    {
                        for (var i = 0; i < read; i++)
                        {
                            CapturedSamples.Add(buffer[i]);
                        }
                    }

                    Interlocked.Add(ref _renderedSamples, read);
                }

                if (read <= 0 || source.IsEndOfStream)
                {
                    Interlocked.Exchange(ref _playing, 0);
                    if (!_shutdown && !_disposed)
                    {
                        Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(reachedEndOfStream: true));
                    }

                    return;
                }

                Thread.Sleep(1);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _playing, 0);
            if (!_shutdown && !_disposed)
            {
                Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(reachedEndOfStream: false, exception: ex));
            }
        }
    }
}
