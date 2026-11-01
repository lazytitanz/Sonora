using Sonora.Audio.Metadata;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.Devices;
using Sonora.Audio.Windows.DSP;
using Sonora.Audio.Windows.Output;

namespace Sonora.Audio.Windows;

/// <summary>
/// Convenience entry point for creating a Sonora engine on Windows.
/// </summary>
public static class SonoraWindows
{
    /// <summary>
    /// Creates an <see cref="AudioEngine"/> configured with Windows decoding, WASAPI shared-mode output,
    /// Multimedia-role output device management, and NAudio-backed DSP effects (equalizer, preamp, limiter).
    /// NAudio types are not exposed through this API.
    /// </summary>
    /// <param name="options">Optional engine options. When <see cref="AudioEngineOptions.EffectFactory"/> is null, a Windows factory is supplied.</param>
    public static AudioEngine CreateEngine(AudioEngineOptions? options = null)
    {
        options ??= new AudioEngineOptions();
        if (options.EffectFactory is null || options.FormatConverter is null)
        {
            options = new AudioEngineOptions
            {
                BufferDuration = options.BufferDuration,
                PrimeDuration = options.PrimeDuration,
                Processors = options.Processors,
                EffectFactory = options.EffectFactory ?? new WindowsAudioEffectFactory(),
                FormatConverter = options.FormatConverter ?? new WindowsAudioFormatConverter(),
            };
        }

        return new AudioEngine(
            new WindowsAudioDecoderFactory(),
            new WindowsAudioOutputFactory(),
            options,
            new WindowsAudioOutputDevices());
    }

    /// <summary>
    /// Creates a platform-neutral metadata reader.
    /// Metadata extraction uses managed ATL.NET and does not require WASAPI or an <see cref="AudioEngine"/>.
    /// Equivalent to constructing <see cref="AudioMetadataReader"/> directly from <c>Sonora.Audio</c>.
    /// </summary>
    public static IAudioMetadataReader CreateMetadataReader() => new AudioMetadataReader();
}
