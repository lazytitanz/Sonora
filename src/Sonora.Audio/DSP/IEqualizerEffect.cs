namespace Sonora.Audio.DSP;

/// <summary>Multiband equalizer effect.</summary>
public interface IEqualizerEffect : IAudioEffect
{
    /// <summary>Number of bands.</summary>
    int BandCount { get; }

    /// <summary>Gets the center/corner frequency of a band in Hz.</summary>
    float GetBandFrequency(int bandIndex);

    /// <summary>Gets the current gain of a band in dB.</summary>
    float GetBandGain(int bandIndex);

    /// <summary>Sets the gain of a band in dB. Applied on the next audio processing block.</summary>
    void SetBandGain(int bandIndex, float gainDb);

    /// <summary>Gets whether a band is enabled.</summary>
    bool GetBandEnabled(int bandIndex);

    /// <summary>Enables or disables a band (disabled bands behave as 0 dB).</summary>
    void SetBandEnabled(int bandIndex, bool enabled);

    /// <summary>Applies a preset (band gains and optional layout must match band count).</summary>
    void ApplyPreset(EqualizerPreset preset);
}
