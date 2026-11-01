using Sonora.Audio.DSP;

namespace Sonora.Audio.Windows.DSP;

/// <summary>
/// Creates NAudio-backed effects without exposing NAudio types through Sonora's public API.
/// </summary>
public sealed class WindowsAudioEffectFactory : IAudioEffectFactory
{
    /// <inheritdoc />
    public IEqualizerEffect CreateEqualizer(EqualizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new NAudioEqualizerEffect(options);
    }

    /// <inheritdoc />
    public IPreampEffect CreatePreamp(float gainDb) => new NAudioPreampEffect(gainDb);

    /// <inheritdoc />
    public ILimiterEffect CreateLimiter(LimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new NAudioLimiterEffect(options);
    }
}
