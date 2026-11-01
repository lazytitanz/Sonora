using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Decoding;
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
    /// and NAudio-backed DSP effects (equalizer, preamp, limiter). NAudio types are not exposed through this API.
    /// </summary>
    /// <param name="options">Optional engine options. When <see cref="AudioEngineOptions.EffectFactory"/> is null, a Windows factory is supplied.</param>
    public static AudioEngine CreateEngine(AudioEngineOptions? options = null)
    {
        options ??= new AudioEngineOptions();
        if (options.EffectFactory is null)
        {
            options = new AudioEngineOptions
            {
                BufferDuration = options.BufferDuration,
                PrimeDuration = options.PrimeDuration,
                Processors = options.Processors,
                EffectFactory = new WindowsAudioEffectFactory(),
            };
        }

        return new AudioEngine(
            new WindowsAudioDecoderFactory(),
            new WindowsAudioOutputFactory(),
            options);
    }
}
