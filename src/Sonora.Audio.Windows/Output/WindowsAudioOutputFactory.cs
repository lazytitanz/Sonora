using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Creates WASAPI shared- or exclusive-mode outputs for a specific endpoint or the Multimedia system default.
/// </summary>
public sealed class WindowsAudioOutputFactory : IAudioOutputFactory
{
    /// <inheritdoc />
    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default) =>
        CreateOutputAsync(AudioOutputCreateRequest.Shared(deviceId), cancellationToken);

    /// <inheritdoc />
    public Task<IAudioOutput> CreateOutputAsync(
        AudioOutputCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAudioOutput output = WasapiAudioOutput.Create(request);
        return Task.FromResult(output);
    }

    /// <inheritdoc />
    public Task<AudioOutputFormatProbeResult> ProbeOutputFormatAsync(
        AudioOutputCreateRequest request,
        AudioFormat preferredSessionFormat,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Mode != AudioOutputMode.Exclusive)
        {
            return Task.FromResult(new AudioOutputFormatProbeResult(
                Supported: true,
                RequiredSessionFormat: preferredSessionFormat));
        }

        var result = WasapiAudioOutput.ProbeExclusiveFormat(request.DeviceId, preferredSessionFormat);
        return Task.FromResult(result);
    }
}
