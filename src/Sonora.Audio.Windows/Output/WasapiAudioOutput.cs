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
internal sealed class WasapiAudioOutput : IAudioOutput
{
    private const int DefaultExclusiveLatencyMs = 50;
    private const int MaxAlignmentRetries = 2;

    private WasapiPlayer _player;
    private readonly string? _endpointId;
    private readonly AudioOutputMode _mode;
    private readonly int _requestedLatencyMs;
    private IPcmSampleSource? _source;
    private PcmSampleProviderAdapter? _adapter;
    private AudioFormat? _format;
    private bool _initialized;
    private bool _shutdown;
    private bool _disposed;

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
            var result = ExclusiveFormatNegotiator.Probe(device, preferred);
            // Probe builds a WasapiPlayer that retains MMDevice and disposes it with the player.
            device = null;
            return result;
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
        _adapter = new PcmSampleProviderAdapter(source);

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
        _player.Play();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
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

        _player.Stop();
    }

    public void Shutdown()
    {
        if (_disposed || _shutdown)
        {
            _source = null;
            return;
        }

        try
        {
            if (_initialized)
            {
                _player.Stop();
            }
        }
        catch (Exception ex)
        {
            _adapter?.Disconnect();
            _source = null;
            throw new AudioOutputException(
                "Failed to stop WASAPI output during shutdown; replacement output was not started.",
                AudioOutputFailureKind.ShutdownFailed,
                ex);
        }

        _adapter?.Disconnect();
        _source = null;
        _shutdown = true;
    }

    public TimeSpan? TryGetRenderedPosition()
    {
        if (!_initialized || _format is null || _adapter is null)
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.PlaybackStopped -= OnPlaybackStopped;

        try
        {
            if (!_shutdown)
            {
                Shutdown();
            }
        }
        catch
        {
            _source = null;
        }

        try
        {
            _player.Dispose();
        }
        catch
        {
            // ignored
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
            .WithEventSync()
            .WithDevice(device)
            .WithLatency(latencyMs);

        return mode == AudioOutputMode.Exclusive
            ? builder.WithExclusiveMode().Build()
            : builder.WithSharedMode().Build();
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

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_shutdown || _disposed)
        {
            return;
        }

        if (e.Exception is not null)
        {
            RaiseStopped(new AudioOutputStoppedEventArgs(
                reachedEndOfStream: false,
                exception: e.Exception is AudioOutputException
                    ? e.Exception
                    : WasapiErrorMapper.Wrap("WASAPI playback stopped unexpectedly.", e.Exception)));
            return;
        }

        var ended = _source?.IsEndOfStream == true;
        RaiseStopped(new AudioOutputStoppedEventArgs(reachedEndOfStream: ended));
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
