using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Creates WASAPI outputs like <see cref="WindowsAudioOutputFactory"/> while retaining the last
/// <see cref="WasapiAudioOutput"/> for opt-in managed-boundary diagnostics.
/// </summary>
internal sealed class TrackingWasapiOutputFactory : IAudioOutputFactory
{
    private readonly WindowsAudioOutputFactory _inner = new();

    public WasapiAudioOutput? LastWasapi { get; private set; }

    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default) =>
        CreateOutputAsync(AudioOutputCreateRequest.Shared(deviceId), cancellationToken);

    public Task<IAudioOutput> CreateOutputAsync(
        AudioOutputCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = WasapiAudioOutput.Create(request);
        LastWasapi = output;
        return Task.FromResult<IAudioOutput>(output);
    }

    public Task<AudioOutputFormatProbeResult> ProbeOutputFormatAsync(
        AudioOutputCreateRequest request,
        AudioFormat preferredSessionFormat,
        CancellationToken cancellationToken = default) =>
        _inner.ProbeOutputFormatAsync(request, preferredSessionFormat, cancellationToken);
}
