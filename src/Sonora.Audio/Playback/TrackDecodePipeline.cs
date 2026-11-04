using System.Runtime.CompilerServices;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Buffers;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Network;
using Sonora.Audio.Sources;

namespace Sonora.Audio.Playback;

/// <summary>
/// One decoder + pump + ring for either the active or next track.
/// Owns the decode worker until it exits; a stop timeout does not release decoder/ring.
/// </summary>
internal sealed class TrackDecodePipeline : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DefaultStopWait = TimeSpan.FromSeconds(2);

    private DecodePump? _pump;
    private int _disposed; // 0 = live, 1 = DisposePending/disposed entry
    private int _resourcesReleased;
    private int _lastStopTimedOut;
    private long _stopPumpTimeoutCount;
    private long _samplesWritten;
    private Action? _onDecodeWorkerAliveAfterStop;
    private Action<int>? _onSamplesWritten;
    private Action<Exception>? _onError;
    private readonly object _lifecycleSync = new();
    private Task? _sharedStopTask;
    private TaskCompletionSource? _resourcesReleasedTcs;

    public TrackDecodePipeline(
        string path,
        IAudioDecoder decoder,
        PcmRingBuffer ring,
        TimeSpan duration,
        AudioSource? source = null,
        INetworkTransportInfo? networkInfo = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        Ring = ring ?? throw new ArgumentNullException(nameof(ring));
        Duration = duration;
        Format = decoder.Format;
        Source = source;
        NetworkInfo = networkInfo;
        Identity = RuntimeHelpers.GetHashCode(this);
    }

    /// <summary>Stable identity for stale-stop / generation checks.</summary>
    public int Identity { get; }

    /// <summary>Identifier string (filesystem path or absolute URI).</summary>
    public string Path { get; }

    /// <summary>Typed source when known; <see langword="null"/> for legacy path-only opens.</summary>
    public AudioSource? Source { get; }

    /// <summary>
    /// Network transport probe captured before format conversion wraps the decoder.
    /// Remains valid while this pipeline owns the (possibly wrapped) decoder.
    /// </summary>
    public INetworkTransportInfo? NetworkInfo { get; }

    public IAudioDecoder Decoder { get; }
    public PcmRingBuffer Ring { get; }
    public TimeSpan Duration { get; }
    public AudioFormat Format { get; }

    /// <summary>True when the decode worker task has not completed.</summary>
    public bool IsPumpRunning
    {
        get
        {
            var pump = Volatile.Read(ref _pump);
            return pump?.IsRunning == true;
        }
    }

    /// <summary>
    /// Completes when the current pump worker has exited (or no pump was started).
    /// Authoritative safety for Seek / restart / dispose of decoder resources.
    /// </summary>
    public Task WorkerShutdownCompleted
    {
        get
        {
            var pump = Volatile.Read(ref _pump);
            return pump?.ShutdownCompleted ?? Task.CompletedTask;
        }
    }

    /// <summary>True after Dispose/DisposeAsync has been entered; no new Seek/StartPump allowed.</summary>
    public bool IsDisposePending => Volatile.Read(ref _disposed) != 0;

    /// <summary>True when decoder and ring have been released.</summary>
    public bool ResourcesReleased => Volatile.Read(ref _resourcesReleased) != 0;

    /// <summary>
    /// Completes when decoder/ring disposal has finished. For DisposeAsync awaiters —
    /// a stop wait timeout alone does not complete this task.
    /// </summary>
    public Task ResourcesReleasedCompleted
    {
        get
        {
            if (Volatile.Read(ref _resourcesReleased) != 0)
            {
                return Task.CompletedTask;
            }

            lock (_lifecycleSync)
            {
                if (Volatile.Read(ref _resourcesReleased) != 0)
                {
                    return Task.CompletedTask;
                }

                _resourcesReleasedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _resourcesReleasedTcs.Task;
            }
        }
    }

    /// <summary>True when the most recent StopPump caller wait timed out before the worker joined.</summary>
    public bool LastStopTimedOut => Volatile.Read(ref _lastStopTimedOut) != 0;

    /// <summary>
    /// Diagnostic: stop wait timed out and the worker is still alive. The pipeline still owns the
    /// worker — this is not an abandoned orphan. Use <see cref="WorkerShutdownCompleted"/> for safety.
    /// </summary>
    public bool HasOrphanDecodeWorker => LastStopTimedOut && IsPumpRunning;

    /// <summary>Cumulative StopPump caller-wait timeouts for this pipeline.</summary>
    public long StopPumpTimeoutCount => Volatile.Read(ref _stopPumpTimeoutCount);

    /// <summary>Optional diagnostics callback when a decode worker is still alive after StopPump timeout.</summary>
    public Action? OnDecodeWorkerAliveAfterStop
    {
        get => _onDecodeWorkerAliveAfterStop;
        set => _onDecodeWorkerAliveAfterStop = value;
    }

    /// <summary>
    /// Interleaved float samples written into the ring by the current/last pump session.
    /// Updated on the decode worker; readable lock-free from other threads.
    /// </summary>
    public long SamplesWritten => Interlocked.Read(ref _samplesWritten);

    /// <summary>
    /// Ring capacity in interleaved float samples for the given format and target buffer duration.
    /// </summary>
    public static int ComputeRingCapacitySamples(AudioFormat format, TimeSpan bufferDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bufferDuration, TimeSpan.Zero);
        return Math.Max(
            format.ChannelCount * 256,
            (int)Math.Ceiling(bufferDuration.TotalSeconds * format.SamplesPerSecond));
    }

    public static TrackDecodePipeline Create(
        AudioSource source,
        IAudioDecoder decoder,
        AudioEngineOptions options,
        INetworkTransportInfo? networkInfo = null,
        TimeSpan? ringBufferDuration = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = ComputeRingCapacitySamples(
            decoder.Format,
            ringBufferDuration ?? options.BufferDuration);
        var ring = new PcmRingBuffer(capacity);
        return new TrackDecodePipeline(
            source.Identifier,
            decoder,
            ring,
            decoder.Duration,
            source,
            networkInfo);
    }

    /// <summary>Legacy path-based factory; prefer the <see cref="AudioSource"/> overload.</summary>
    public static TrackDecodePipeline Create(
        string path,
        IAudioDecoder decoder,
        AudioEngineOptions options,
        INetworkTransportInfo? networkInfo = null,
        TimeSpan? ringBufferDuration = null)
    {
        var capacity = ComputeRingCapacitySamples(
            decoder.Format,
            ringBufferDuration ?? options.BufferDuration);
        var ring = new PcmRingBuffer(capacity);
        return new TrackDecodePipeline(path, decoder, ring, decoder.Duration, source: null, networkInfo);
    }

    /// <summary>
    /// Installs or replaces pump callbacks without restarting the worker.
    /// Used after gapless/crossfade promote so the active track reports decode progress and errors.
    /// </summary>
    public void SetPumpCallbacks(Action<int> onSamplesWritten, Action<Exception>? onError)
    {
        ArgumentNullException.ThrowIfNull(onSamplesWritten);
        Volatile.Write(ref _onSamplesWritten, onSamplesWritten);
        Volatile.Write(ref _onError, onError);
    }

    public void StartPump(
        IReadOnlyList<IAudioProcessor> processors,
        Action<int> onSamplesWritten,
        Action<Exception>? onError)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SetPumpCallbacks(onSamplesWritten, onError);

        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var existing = _pump;
            if (existing is not null)
            {
                if (existing.IsRunning || !existing.ShutdownCompleted.IsCompleted)
                {
                    // Never create a second worker while the previous one still owns the decoder.
                    throw new InvalidOperationException(
                        "Cannot start a decode pump while a previous decode worker is still alive.");
                }
            }

            Interlocked.Exchange(ref _samplesWritten, 0);
            _pump = new DecodePump(
                Decoder,
                Ring,
                processors,
                OnPumpSamplesWritten,
                OnPumpError,
                onStillAliveAfterCancel: () =>
                {
                    try
                    {
                        _onDecodeWorkerAliveAfterStop?.Invoke();
                    }
                    catch
                    {
                        // diagnostics must not throw
                    }
                });
            Volatile.Write(ref _lastStopTimedOut, 0);
            _sharedStopTask = null;
            _pump.Start();
        }
    }

    private void OnPumpSamplesWritten(int n)
    {
        if (n > 0)
        {
            Interlocked.Add(ref _samplesWritten, n);
        }

        try
        {
            Volatile.Read(ref _onSamplesWritten)?.Invoke(n);
        }
        catch
        {
            // player callbacks must not tear down the decode worker
        }
    }

    private void OnPumpError(Exception ex)
    {
        try
        {
            Volatile.Read(ref _onError)?.Invoke(ex);
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// Stops the decode pump. May block up to two seconds waiting for the worker.
    /// Callers must not hold the player command gate while invoking this.
    /// Timeout does not mean the worker exited.
    /// </summary>
    public void StopPump()
    {
        StopPumpAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously stops the decode pump. Callers must not hold the player command gate.
    /// Concurrent callers share one stop. A timed-out wait leaves the worker owned and tracked.
    /// </summary>
    public Task StopPumpAsync() => StopPumpAsync(DefaultStopWait);

    public async Task StopPumpAsync(TimeSpan callerWait)
    {
        Task stopTask;
        DecodePump? pump;
        lock (_lifecycleSync)
        {
            pump = _pump;
            if (pump is null)
            {
                Volatile.Write(ref _lastStopTimedOut, 0);
                return;
            }

            if (_sharedStopTask is null)
            {
                _sharedStopTask = StopCoreAsync(pump);
            }

            stopTask = _sharedStopTask;
        }

        var completed = await Task.WhenAny(stopTask, Task.Delay(callerWait)).ConfigureAwait(false);
        if (completed != stopTask)
        {
            Volatile.Write(ref _lastStopTimedOut, 1);
            Interlocked.Increment(ref _stopPumpTimeoutCount);
            return;
        }

        Volatile.Write(ref _lastStopTimedOut, 0);
        await stopTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the decode worker without waiting. Idempotent. Must not be used as proof of exit.
    /// </summary>
    public void RequestPumpStop()
    {
        lock (_lifecycleSync)
        {
            var pump = _pump;
            if (pump is null)
            {
                return;
            }

            _sharedStopTask ??= StopCoreAsync(pump);
        }
    }

    private async Task StopCoreAsync(DecodePump pump)
    {
        // Full wait — authoritative worker exit (no caller timeout here).
        await pump.RequestStop().ConfigureAwait(false);
        pump.Dispose();
    }

    /// <summary>
    /// True when the decode worker has exited and decoder Seek/Dispose is safe for this pipeline.
    /// </summary>
    public bool IsDecoderIdle =>
        Volatile.Read(ref _disposed) == 0
        && !IsPumpRunning
        && WorkerShutdownCompleted.IsCompleted;

    public void Prime(TimeSpan primeDuration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var target = (int)Math.Ceiling(primeDuration.TotalSeconds * Format.SamplesPerSecond);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Ring.Count < target && !Ring.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        BeginDispose();
        try
        {
            StopPump();
        }
        catch
        {
            // best-effort; resources still deferred until worker exit
        }

        // Sync Dispose cannot block forever on a stuck native Read — schedule release and wait briefly.
        var released = ResourcesReleasedCompleted;
        if (!released.IsCompleted)
        {
            ScheduleResourceReleaseAfterWorker();
            released.Wait(DefaultStopWait);
        }
    }

    public async ValueTask DisposeAsync()
    {
        BeginDispose();
        try
        {
            // Request stop but do not treat caller wait timeout as dispose completion.
            await StopPumpAsync(DefaultStopWait).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }

        ScheduleResourceReleaseAfterWorker();
        await ResourcesReleasedCompleted.ConfigureAwait(false);
    }

    private void BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lifecycleSync)
        {
            _resourcesReleasedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ScheduleResourceReleaseAfterWorker()
    {
        if (Volatile.Read(ref _resourcesReleased) != 0)
        {
            return;
        }

        var pump = Volatile.Read(ref _pump);
        var worker = pump?.ShutdownCompleted ?? Task.CompletedTask;

        // Async continuation — never run resource dispose synchronously on the decode worker thread.
        _ = worker.ContinueWith(
            static (_, state) =>
            {
                var self = (TrackDecodePipeline)state!;
                self.ReleaseResourcesOnce();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseResourcesOnce()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
        {
            return;
        }

        try
        {
            Ring.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            Decoder.Dispose();
        }
        catch
        {
            // ignored
        }

        TaskCompletionSource? tcs;
        lock (_lifecycleSync)
        {
            tcs = _resourcesReleasedTcs;
            _pump = null;
            _sharedStopTask = null;
        }

        tcs?.TrySetResult();
    }
}
