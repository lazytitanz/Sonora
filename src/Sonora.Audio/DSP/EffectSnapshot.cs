namespace Sonora.Audio.DSP;

/// <summary>Immutable snapshot of the active effect chain for the audio thread.</summary>
internal sealed class EffectSnapshot
{
    public static EffectSnapshot Empty { get; } = new(Array.Empty<IAudioEffectRuntime>());

    public EffectSnapshot(IAudioEffectRuntime[] effects)
    {
        Effects = effects;
    }

    public IAudioEffectRuntime[] Effects { get; }
}
