using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;

namespace Sonora.Audio.Windows.Tests.Fakes;

/// <summary>
/// Pull-loop output for non-hardware Windows seek/gapless/diagnostic tests.
/// Optionally retains interleaved float PCM for offline comparison (not RT-safe for long captures in production).
/// </summary>
internal sealed class PullingFakeOutput : IAudioOutput
{
    private readonly int _blockSamples;
    private readonly TimeSpan _latency;
    private readonly bool _capture;
    private IPcmSampleSource? _source;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private int _playing;
    private bool _disposed;
    private long _rendered;

    public PullingFakeOutput(
        int blockSamples = 256,
        TimeSpan? latency = null,
        string? endpointId = null,
        bool captureSamples = false)
    {
        _blockSamples = Math.Max(32, blockSamples);
        _latency = latency ?? TimeSpan.FromMilliseconds(5);
        _capture = captureSamples;
        EndpointId = endpointId;
    }

    public AudioFormat? Format { get; private set; }
    public string? EndpointId { get; }
    public TimeSpan AverageLatency => _latency;
    public TimeSpan CurrentLatency => _playing == 1 ? _latency : _latency;
    public event EventHandler<AudioOutputStoppedEventArgs>? Stopped;
    public long RenderedSamples => Interlocked.Read(ref _rendered);
    public List<float> CapturedSamples { get; } = new();
    public int ReadCount { get; private set; }
    public int PlayCount { get; private set; }

    /// <summary>True when no pull loop is running.</summary>
    public bool IsPullLoopIdle
    {
        get
        {
            var thread = _thread;
            return thread is null || !thread.IsAlive;
        }
    }

    public void Initialize(IPcmSampleSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Format = source.Format;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PlayCount++;
        if (Interlocked.Exchange(ref _playing, 1) == 1)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "Sonora.PullingFakeOutput",
        };
        _thread.Start();
    }

    public void Pause() => StopPull(raiseStopped: false);

    public void Stop() => StopPull(raiseStopped: true);

    public TimeSpan? TryGetRenderedPosition()
    {
        if (Format is not { } format || format.SampleRate <= 0)
        {
            return null;
        }

        var samples = Interlocked.Read(ref _rendered);
        var frames = samples / Math.Max(1, format.ChannelCount);
        return TimeSpan.FromSeconds(frames / (double)format.SampleRate);
    }

    public void Shutdown() => StopPull(raiseStopped: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPull(raiseStopped: false);
    }

    private void StopPull(bool raiseStopped)
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

        var thread = _thread;
        _thread = null;
        if (thread is { IsAlive: true })
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        _cts?.Dispose();
        _cts = null;

        if (raiseStopped)
        {
            try
            {
                Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(reachedEndOfStream: _source?.IsEndOfStream == true));
            }
            catch
            {
                // ignored
            }
        }
    }

    private void Loop(CancellationToken token)
    {
        var buffer = new float[_blockSamples];
        while (!token.IsCancellationRequested && Volatile.Read(ref _playing) == 1)
        {
            var source = _source;
            if (source is null)
            {
                break;
            }

            int read;
            try
            {
                read = source.Read(buffer);
                ReadCount++;
            }
            catch
            {
                break;
            }

            if (read <= 0 || source.IsEndOfStream)
            {
                try
                {
                    Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(reachedEndOfStream: true));
                }
                catch
                {
                    // ignored
                }

                Interlocked.Exchange(ref _playing, 0);
                break;
            }

            if (_capture && read > 0)
            {
                lock (CapturedSamples)
                {
                    for (var i = 0; i < read; i++)
                    {
                        CapturedSamples.Add(buffer[i]);
                    }
                }
            }

            Interlocked.Add(ref _rendered, read);
            // Wake promptly on cancel; 1 ms pacing keeps pull ahead of realtime for offline capture.
            if (token.WaitHandle.WaitOne(1))
            {
                break;
            }
        }
    }
}

internal sealed class PullingFakeOutputFactory : IAudioOutputFactory
{
    private readonly bool _captureSamples;
    private readonly Func<PullingFakeOutput>? _create;

    public PullingFakeOutputFactory(bool captureSamples = false)
    {
        _captureSamples = captureSamples;
    }

    public PullingFakeOutputFactory(Func<PullingFakeOutput> create)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    /// <summary>Most recently created output (when using the capture/default constructors).</summary>
    public PullingFakeOutput? LastCreated { get; private set; }

    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default) =>
        CreateOutputAsync(AudioOutputCreateRequest.Shared(deviceId), cancellationToken);

    public Task<IAudioOutput> CreateOutputAsync(
        AudioOutputCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = _create?.Invoke()
            ?? new PullingFakeOutput(
                endpointId: request.DeviceId ?? "pulling-fake",
                captureSamples: _captureSamples);
        LastCreated = output;
        return Task.FromResult<IAudioOutput>(output);
    }

    public Task<AudioOutputFormatProbeResult> ProbeOutputFormatAsync(
        AudioOutputCreateRequest request,
        AudioFormat preferredSessionFormat,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AudioOutputFormatProbeResult(true, preferredSessionFormat));
    }
}
