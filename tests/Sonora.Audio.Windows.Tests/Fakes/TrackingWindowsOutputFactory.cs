using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;
using Sonora.Audio.Windows.Output;

namespace Sonora.Audio.Windows.Tests.Fakes;

/// <summary>
/// Test alias for <see cref="TrackingWasapiOutputFactory"/>.
/// </summary>
internal sealed class TrackingWindowsOutputFactory : IAudioOutputFactory
{
    private readonly TrackingWasapiOutputFactory _inner = new();

    public WasapiAudioOutput? LastWasapi => _inner.LastWasapi;

    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default) =>
        _inner.CreateOutputAsync(deviceId, cancellationToken);

    public Task<IAudioOutput> CreateOutputAsync(
        AudioOutputCreateRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.CreateOutputAsync(request, cancellationToken);

    public Task<AudioOutputFormatProbeResult> ProbeOutputFormatAsync(
        AudioOutputCreateRequest request,
        AudioFormat preferredSessionFormat,
        CancellationToken cancellationToken = default) =>
        _inner.ProbeOutputFormatAsync(request, preferredSessionFormat, cancellationToken);
}
