namespace Sonora.Audio.DSP;

/// <summary>
/// Creates platform-backed effect instances. Registered by the Windows backend; core remains NAudio-free.
/// </summary>
public interface IAudioEffectFactory
{
    /// <summary>Creates a multiband equalizer.</summary>
    IEqualizerEffect CreateEqualizer(EqualizerOptions options);

    /// <summary>Creates a preamp with the given initial gain in dB.</summary>
    IPreampEffect CreatePreamp(float gainDb);

    /// <summary>Creates a peak limiter for clipping protection.</summary>
    ILimiterEffect CreateLimiter(LimiterOptions options);
}
