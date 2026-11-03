using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// WASAPI output using NAudio <see cref="WasapiPlayer"/> (shared or exclusive).
/// Pulls PCM from an <see cref="IPcmSampleSource"/> (ring buffer); never decodes on the callback path.
/// </summary>
internal sealed class WasapiAudioOutput : IAudioOutput, IAsyncDisposable
{
    /// <summary>
    /// Exclusive event-sync at 50ms still trips native GetBuffer AVs on some HDMI/AMD endpoints
    /// during Shared→Exclusive rebinds. Prefer a slightly larger exclusive period.
    /// </summary>
    private const int DefaultExclusiveLatencyMs = 150;

    /// <summary>
    /// Brief pause after Stop/Join before releasing <see cref="WasapiPlayer"/> COM objects.
    /// Shared→Exclusive rebinds still race some drivers if FinalRelease runs too close to Stop.
    /// </summary>
    private static readonly TimeSpan ComReleaseAfterStopGrace = TimeSpan.FromMilliseconds(75);

    private const int MaxAlignmentRetries = 2;

    private WasapiPlayer _player;
    private readonly string? _endpointId;
    private readonly AudioOutputMode _mode;
    private readonly int _requestedLatencyMs;
    /// <summary>
    /// Serializes lifecycle decisions only. Never hold across <see cref="WasapiPlayer.Stop"/> Join.
    /// </summary>
    private readonly object _lifecycleSync = new();
    private IPcmSampleSource? _source;
    private PcmSampleProviderAdapter? _adapter;
    private AudioFormat? _format;
    private bool _initialized;
    private bool _shutdown;
    private bool _disposed;
    /// <summary>Shared completion for the in-flight Stop/Join. Concurrent Stop/Shutdown await this.</summary>
    private Task? _stopCompletion;
    /// <summary>Shared completion for Dispose. Concurrent Dispose/DisposeAsync await this.</summary>
    private TaskCompletionSource? _disposeTcs;
    /// <summary>Thread that owns DisposeCore so nested Shutdown does not wait on itself.</summary>
    private int _disposeOwnerThreadId;
    /// <summary>Incremented on each <see cref="Play"/> so a stale EOS stop cannot stop a newer session.</summary>
    private int _playSession;

    private WasapiAudioOutput(WasapiPlayer player, string? endpointId, AudioOutputMode mode, int requestedLatencyMs)
    {
        _player = player;
        _endpointId = endpointId;
        _mode = mode;
        _requestedLatencyMs = requestedLatencyMs;
        _player.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>Creates shared-mode WASAPI output (backward-compatible entry point).</summary>
    public static WasapiAudioOutput Create(string? deviceId) =>
        Create(AudioOutputCreateRequest.Shared(deviceId));

    /// <summary>Creates WASAPI output for the given request.</summary>
    public static WasapiAudioOutput Create(AudioOutputCreateRequest request)
    {
        MMDevice? ownedDevice = null;
        try
        {
            ownedDevice = ResolveDevice(request.DeviceId);
            var endpointId = ownedDevice.ID;
            var latencyMs = ResolveLatencyMs(request);
            var player = BuildPlayer(ownedDevice, request.Mode, latencyMs);

            // WasapiPlayer retains the MMDevice; do not dispose ownedDevice here.
            ownedDevice = null;
            return new WasapiAudioOutput(player, endpointId, request.Mode, latencyMs);
        }
        catch (AudioException)
        {
            ownedDevice?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            ownedDevice?.Dispose();
            throw WasapiErrorMapper.Wrap(
                request.Mode == AudioOutputMode.Exclusive
                    ? "Failed to create the WASAPI exclusive-mode output device."
                    : "Failed to create the WASAPI shared-mode output device.",
                ex);
        }
    }

    /// <summary>
    /// Non-destructive exclusive format probe. Does not Init a PCM consumer.
    /// </summary>
    public static AudioOutputFormatProbeResult ProbeExclusiveFormat(string? deviceId, AudioFormat preferred)
    {
        MMDevice? device = null;
        try
        {
            device = ResolveDevice(deviceId);
            // Query-only probe: does not Initialize or take exclusive ownership of the endpoint.
            return ExclusiveFormatNegotiator.Probe(device, preferred);
        }
        catch (AudioException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = WasapiErrorMapper.Classify(ex);
            if (kind == AudioOutputFailureKind.Unspecified)
            {
                kind = AudioOutputFailureKind.InitializationFailed;
            }

            return new AudioOutputFormatProbeResult(false, preferred, kind);
        }
        finally
        {
            device?.Dispose();
        }
    }

    public AudioOutputMode Mode => _mode;

    public AudioFormat? Format => _format;

    public string? EndpointId => _endpointId;

    public TimeSpan AverageLatency
    {
        get
        {
            try
            {
                return _player.AverageLatency;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
    }

    public TimeSpan CurrentLatency
    {
        get
        {
            try
            {
                return _player.CurrentLatency;
            }
            catch
            {
                return AverageLatency;
            }
        }
    }

    public event EventHandler<AudioOutputStoppedEventArgs>? Stopped;

    public void Initialize(IPcmSampleSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (_initialized)
        {
            throw new InvalidOperationException("WASAPI output can only be initialized once. Create a new output instance.");
        }

        _source = source;
        _format = source.Format;
        // When the PCM source ends, stop WasapiPlayer off the render thread so exclusive mode
        // does not ReleaseBuffer(0) → AUDCLNT_E_BUFFER_SIZE_ERROR (0x88890016).
        _adapter = new PcmSampleProviderAdapter(source, RequestStopAfterEndOfStream);

        var latencyMs = _requestedLatencyMs;
        Exception? last = null;
        for (var attempt = 0; attempt <= MaxAlignmentRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    RebuildPlayer(latencyMs);
                }

                _player.Init(_adapter);
                _initialized = true;
                return;
            }
            catch (Exception ex) when (
                attempt < MaxAlignmentRetries
                && WasapiErrorMapper.IsRecoverableBufferAlignmentFailure(ex))
            {
                last = ex;
                // Widen the requested period toward a safer exclusive/shared default.
                latencyMs = Math.Min(Math.Max(latencyMs * 2, latencyMs + 10), 200);
            }
            catch (Exception ex)
            {
                _adapter = null;
                _source = null;
                _format = null;
                throw WasapiErrorMapper.Wrap(
                    _mode == AudioOutputMode.Exclusive
                        ? "Failed to initialize WASAPI exclusive-mode playback."
                        : "Failed to initialize WASAPI shared-mode playback.",
                    ex);
            }
        }

        _adapter = null;
        _source = null;
        _format = null;
        throw WasapiErrorMapper.Wrap(
            "Failed to initialize WASAPI playback after buffer-alignment retries.",
            last ?? new InvalidOperationException("Unknown Init failure."));
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        // Clear exclusive silence-pause, EOS latch, and allow Stopped after a prior EOS stop.
        _adapter?.SetPaused(false);
        _adapter?.ResetEndOfStreamLatch();
        Interlocked.Exchange(ref _stoppedRaised, 0);
        Interlocked.Exchange(ref _eosStopRequested, 0);
        Interlocked.Increment(ref _playSession);

        // A completed prior Stop must not block a new Play session from starting Stop again.
        lock (_lifecycleSync)
        {
            if (_stopCompletion?.IsCompleted == true)
            {
                _stopCompletion = null;
            }
        }

        _player.Play();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            return;
        }

        if (_mode == AudioOutputMode.Exclusive)
        {
            // NAudio WasapiPlayer.Pause only flips PlaybackState and skips FillBuffer while leaving
            // IAudioClient running. Exclusive event mode then typically repeats the last period
            // (~1 ms loop). Keep the client/thread running and feed silence from the adapter
            // instead — avoids the loop without Stop()/Restart races that can AV GetBuffer.
            _adapter?.SetPaused(true);
            return;
        }

        _player.Pause();
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            return;
        }

        StopAndJoinShared();
    }

    public void Shutdown()
    {
        Task? existingDispose;
        lock (_lifecycleSync)
        {
            if (_shutdown)
            {
                _source = null;
                return;
            }

            // Concurrent Dispose owns full teardown — wait for its shared completion.
            // Skip when DisposeCore is calling us (dispose TCS exists but we are the owner).
            existingDispose = _disposeOwnerThreadId == Environment.CurrentManagedThreadId
                ? null
                : _disposeTcs?.Task;
        }

        if (existingDispose is not null)
        {
            existingDispose.GetAwaiter().GetResult();
            _source = null;
            return;
        }

        ShutdownCore();
    }

    private void ShutdownCore()
    {
        if (_shutdown)
        {
            _source = null;
            return;
        }

        // Disconnect before Stop/Join so the render thread feeds silence instead of touching the
        // live sample source / ring while we tear the client down.
        _adapter?.Disconnect();
        _source = null;

        try
        {
            if (_initialized)
            {
                StopAndJoinShared();
            }
        }
        catch (Exception ex)
        {
            lock (_lifecycleSync)
            {
                _shutdown = true;
            }

            throw new AudioOutputException(
                "Failed to stop WASAPI output during shutdown; replacement output was not started.",
                AudioOutputFailureKind.ShutdownFailed,
                ex);
        }

        lock (_lifecycleSync)
        {
            _shutdown = true;
        }
    }

    public TimeSpan? TryGetRenderedPosition()
    {
        if (!_initialized || _format is null || _adapter is null || _shutdown || _disposed)
        {
            return null;
        }

        try
        {
            var bytes = _player.GetPosition();
            var format = _adapter.WaveFormat;
            if (format.AverageBytesPerSecond <= 0)
            {
                return null;
            }

            return TimeSpan.FromSeconds(bytes / (double)format.AverageBytesPerSecond);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        var tcs = BeginDisposeShared(out var owns);
        if (!owns)
        {
            tcs.Task.GetAwaiter().GetResult();
            return;
        }

        try
        {
            DisposeCore();
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            // Dispose must not throw for secondary failures after best-effort teardown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        var tcs = BeginDisposeShared(out var owns);
        if (!owns)
        {
            await tcs.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            // Stop/Join and COM release must not run on a UI sync context.
            await Task.Run(DisposeCore).ConfigureAwait(false);
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    /// <summary>
    /// One Join owner; concurrent Stop/Shutdown/Dispose await the same completion.
    /// Never call while holding <see cref="_lifecycleSync"/>.
    /// </summary>
    private void StopAndJoinShared()
    {
        TaskCompletionSource? owned = null;
        Task toAwait;
        lock (_lifecycleSync)
        {
            if (_stopCompletion is not null)
            {
                toAwait = _stopCompletion;
            }
            else
            {
                owned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopCompletion = owned.Task;
                toAwait = owned.Task;
            }
        }

        if (owned is null)
        {
            toAwait.GetAwaiter().GetResult();
            return;
        }

        try
        {
            _adapter?.SetPaused(false);
            _player.Stop();
            owned.TrySetResult();
        }
        catch (Exception ex)
        {
            owned.TrySetException(ex);
            throw;
        }
    }

    private TaskCompletionSource BeginDisposeShared(out bool owns)
    {
        lock (_lifecycleSync)
        {
            if (_disposeTcs is not null)
            {
                owns = false;
                return _disposeTcs;
            }

            _disposeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeOwnerThreadId = Environment.CurrentManagedThreadId;
            owns = true;
            return _disposeTcs;
        }
    }

    private void DisposeCore()
    {
        // Ensure nested Shutdown sees this thread as the dispose owner (DisposeAsync hops threads).
        _disposeOwnerThreadId = Environment.CurrentManagedThreadId;

        // Shutdown (Stop+Join+Disconnect) must run before marking disposed so Shutdown is not a no-op.
        try
        {
            if (!_shutdown)
            {
                ShutdownCore();
            }
        }
        catch
        {
            _source = null;
        }

        _player.PlaybackStopped -= OnPlaybackStopped;

        // Let the driver finish releasing the endpoint after Join before FinalRelease of the
        // AudioClient / AudioRenderClient. Skipping this still allows intermittent GetBuffer AVs
        // when the next exclusive client opens the same endpoint immediately.
        try
        {
            Thread.Sleep(ComReleaseAfterStopGrace);
        }
        catch (ThreadInterruptedException)
        {
            // ignored — best-effort settle
        }

        try
        {
            // Prefer sync Dispose here. NAudio DisposeAsync joins via Task.Run and posts
            // its continuation to the ambient SynchronizationContext — calling it with
            // GetResult() on a WinForms UI thread deadlocks (AppHang).
            _player.Dispose();
        }
        catch
        {
            // ignored
        }

        lock (_lifecycleSync)
        {
            _disposed = true;
            _shutdown = true;
        }
    }

    private void RebuildPlayer(int latencyMs)
    {
        _player.PlaybackStopped -= OnPlaybackStopped;
        try
        {
            _player.Dispose();
        }
        catch
        {
            // ignored
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        if (_endpointId is null)
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        else
        {
            device = enumerator.GetDevice(_endpointId);
        }

        _player = BuildPlayer(device, _mode, latencyMs);
        _player.PlaybackStopped += OnPlaybackStopped;
    }

    private static WasapiPlayer BuildPlayer(MMDevice device, AudioOutputMode mode, int latencyMs)
    {
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithLatency(latencyMs);

        // Exclusive + event sync fills the full WASAPI period every wake (padding forced to 0).
        // That path has caused process-killing IAudioRenderClient.GetBuffer AVs (0xC0000005)
        // on some endpoints when rebinding Shared→Exclusive after a device switch. Polling
        // exclusive uses CurrentPadding and is materially more stable on those drivers.
        if (mode == AudioOutputMode.Exclusive)
        {
            return builder.WithExclusiveMode().WithPollingSync().Build();
        }

        return builder.WithSharedMode().WithEventSync().Build();
    }

    private static int ResolveLatencyMs(AudioOutputCreateRequest request)
    {
        if (request.RequestedLatency is { } latency)
        {
            return Math.Clamp((int)Math.Round(latency.TotalMilliseconds), 5, 500);
        }

        return request.Mode == AudioOutputMode.Exclusive ? DefaultExclusiveLatencyMs : 200;
    }

    private static MMDevice ResolveDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (deviceId is null)
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                throw new AudioDeviceException("No default Windows multimedia audio output device is available.");
            }

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        var device = enumerator.GetDevice(deviceId);
        if (device.DataFlow != DataFlow.Render)
        {
            device.Dispose();
            throw new AudioDeviceException($"Device '{deviceId}' is not an audio render endpoint.");
        }

        if (device.State != DeviceState.Active)
        {
            var name = device.FriendlyName;
            device.Dispose();
            throw new AudioDeviceException($"Output device '{name}' is not active (state: {device.State}).");
        }

        return device;
    }

    private int _stoppedRaised;
    private int _eosStopRequested;

    private void RequestStopAfterEndOfStream()
    {
        if (Interlocked.Exchange(ref _eosStopRequested, 1) == 1)
        {
            return;
        }

        var session = Volatile.Read(ref _playSession);

        // Never Stop() on the WASAPI render thread — queue a worker.
        // Use shared Stop so a concurrent Shutdown/Dispose awaits the same Join instead of
        // skipping it and releasing COM under a live render thread.
        _ = Task.Run(() =>
        {
            try
            {
                if (_shutdown || _disposed)
                {
                    return;
                }

                if (Volatile.Read(ref _playSession) != session)
                {
                    return;
                }

                StopAndJoinShared();
            }
            catch
            {
                // Stop races with an already-faulted PlayThread are benign.
            }
        });
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_shutdown || _disposed)
        {
            return;
        }

        // NAudio may raise PlaybackStopped more than once for the same failure; deliver one event.
        if (Interlocked.Exchange(ref _stoppedRaised, 1) == 1)
        {
            return;
        }

        if (e.Exception is not null)
        {
            // Exclusive event-driven mode can surface AUDCLNT_E_BUFFER_SIZE_ERROR (0x88890016) when
            // the final period is short or zero. If the PCM source has already ended (or we already
            // requested a clean stop after EOS padding), treat that as clean EOS rather than a
            // device-loss recovery loop.
            var ended = _source?.IsEndOfStream == true || Volatile.Read(ref _eosStopRequested) == 1;
            if (ended && WasapiErrorMapper.IsRecoverableBufferAlignmentFailure(e.Exception))
            {
                RaiseStopped(new AudioOutputStoppedEventArgs(reachedEndOfStream: true));
                return;
            }

            RaiseStopped(new AudioOutputStoppedEventArgs(
                reachedEndOfStream: false,
                exception: e.Exception is AudioOutputException
                    ? e.Exception
                    : WasapiErrorMapper.Wrap("WASAPI playback stopped unexpectedly.", e.Exception)));
            return;
        }

        var naturalEnd = _source?.IsEndOfStream == true || Volatile.Read(ref _eosStopRequested) == 1;
        RaiseStopped(new AudioOutputStoppedEventArgs(reachedEndOfStream: naturalEnd));
    }

    private void RaiseStopped(AudioOutputStoppedEventArgs args)
    {
        try
        {
            Stopped?.Invoke(this, args);
        }
        catch
        {
            // ignored
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Output has not been initialized.");
        }
    }
}
