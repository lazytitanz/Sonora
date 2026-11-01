namespace Sonora.Audio.DSP;

/// <summary>Preamp / gain effect with decibel control, distinct from <see cref="AudioPlayer.Volume"/>.</summary>
public interface IPreampEffect : IAudioEffect
{
    /// <summary>Gain in decibels (0 dB = unity). Applied on the next audio processing block.</summary>
    float GainDb { get; set; }
}
