using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Creates WASAPI shared-mode outputs for the default render device.
/// </summary>
public sealed class WindowsAudioOutputFactory : IAudioOutputFactory
{
    /// <inheritdoc />
    public IAudioOutput CreateDefaultOutput() => new WasapiAudioOutput();
}
