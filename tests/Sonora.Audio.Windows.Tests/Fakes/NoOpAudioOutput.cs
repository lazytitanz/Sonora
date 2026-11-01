using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Windows.Tests.Fakes;

/// <summary>
/// Output that initializes without WASAPI. Suitable for Load/Seek/DSP tests that never Play.
/// </summary>
internal sealed class NoOpAudioOutput : IAudioOutput
{
    private IPcmSampleSource? _source;
    private bool _disposed;

    public NoOpAudioOutput(string? endpointId = null)
    {
        EndpointId = endpointId;
    }

    public AudioFormat? Format { get; private set; }
    public string? EndpointId { get; }
    public TimeSpan AverageLatency => TimeSpan.FromMilliseconds(20);
    public TimeSpan CurrentLatency => AverageLatency;
    public event EventHandler<AudioOutputStoppedEventArgs>? Stopped;

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
    }

    public void Pause()
    {
    }

    public void Stop()
    {
        // Keep event available for symmetry; never raise EOS from this stub.
        _ = Stopped;
    }

    public TimeSpan? TryGetRenderedPosition() => TimeSpan.Zero;

    public void Dispose() => _disposed = true;
}

internal sealed class NoOpAudioOutputFactory : IAudioOutputFactory
{
    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAudioOutput>(new NoOpAudioOutput(deviceId));
    }
}
