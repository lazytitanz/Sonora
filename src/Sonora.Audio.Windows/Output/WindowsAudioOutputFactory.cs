using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Creates WASAPI shared-mode outputs for a specific endpoint or the Multimedia system default.
/// </summary>
public sealed class WindowsAudioOutputFactory : IAudioOutputFactory
{
    /// <inheritdoc />
    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Construction is synchronous (WithDevice + Build); wrap for the async factory contract.
        IAudioOutput output = WasapiAudioOutput.Create(deviceId);
        return Task.FromResult(output);
    }
}
