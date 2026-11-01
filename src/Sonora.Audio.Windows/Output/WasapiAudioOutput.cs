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
    private IPcmSampleSource? _source;
    private PcmSampleProviderAdapter? _adapter;
    private AudioFormat? _format;
    private bool _initialized;
    private bool _disposed;

    public WasapiAudioOutput()
    {
        try
        {
            _player = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithEventSync()
                .Build();
        }
        catch (Exception ex)
        {
            throw new AudioOutputException("Failed to create the WASAPI shared-mode output device.", ex);
        }

        _player.PlaybackStopped += OnPlaybackStopped;
    }

    public AudioFormat? Format => _format;

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
            _player.Stop();
        }
        catch
        {
            // ignored
        }

        _player.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
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
