using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// WASAPI shared-mode output using NAudio <see cref="WasapiPlayer"/>.
/// Pulls PCM exclusively from an <see cref="IPcmSampleSource"/> (ring buffer); never decodes on the callback path.
/// Device removal is observed via <see cref="WasapiPlayer.PlaybackStopped"/> failures.
/// </summary>
internal sealed class WasapiAudioOutput : IAudioOutput
{
    private readonly WasapiPlayer _player;
    private readonly string? _endpointId;
    private IPcmSampleSource? _source;
    private PcmSampleProviderAdapter? _adapter;
    private AudioFormat? _format;
    private bool _initialized;
    private bool _shutdown;
    private bool _disposed;

    private WasapiAudioOutput(WasapiPlayer player, string? endpointId)
    {
        _player = player;
        _endpointId = endpointId;
        _player.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>
    /// Creates shared-mode WASAPI output for the Multimedia default endpoint, or a specific endpoint id.
    /// </summary>
    public static WasapiAudioOutput Create(string? deviceId)
    {
        MMDevice? ownedDevice = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (deviceId is null)
            {
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    throw new AudioDeviceException("No default Windows multimedia audio output device is available.");
                }

                ownedDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            else
            {
                ownedDevice = enumerator.GetDevice(deviceId);
                if (ownedDevice.DataFlow != DataFlow.Render)
                {
                    throw new AudioDeviceException($"Device '{deviceId}' is not an audio render endpoint.");
                }

                if (ownedDevice.State != DeviceState.Active)
                {
                    throw new AudioDeviceException(
                        $"Output device '{ownedDevice.FriendlyName}' is not active (state: {ownedDevice.State}).");
                }
            }

            var endpointId = ownedDevice.ID;
            var player = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithDevice(ownedDevice)
                .Build();

            // WasapiPlayer retains the MMDevice; do not dispose ownedDevice here.
            ownedDevice = null;
            return new WasapiAudioOutput(player, endpointId);
        }
        catch (AudioException)
        {
            ownedDevice?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            ownedDevice?.Dispose();
            throw new AudioOutputException("Failed to create the WASAPI shared-mode output device.", ex);
        }
    }

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

        try
        {
            _player.Init(_adapter);
            _initialized = true;
        }
        catch (Exception ex)
        {
            throw new AudioOutputException("Failed to initialize WASAPI shared-mode playback.", ex);
        }
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
            // Still sever the source so late callbacks cannot read TransitionSampleSource.
            _adapter?.Disconnect();
            _source = null;
            throw new AudioOutputException(
                "Failed to stop WASAPI output during shutdown; replacement output was not started.",
                ex);
        }

        // Sever the PCM source so any late callback cannot read TransitionSampleSource.
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
            // Dispose must not throw; Shutdown failures are reported by switch/recovery paths.
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
                    : new AudioOutputException("WASAPI playback stopped unexpectedly.", e.Exception)));
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
