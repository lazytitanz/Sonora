namespace Sonora.Audio.DSP;

/// <summary>Immutable configuration for a single equalizer band.</summary>
public sealed class EqualizerBandConfig
{
    /// <summary>Creates a band configuration.</summary>
    public EqualizerBandConfig(
        EqualizerBandType type,
        float frequencyHz,
        float gainDb = 0f,
        float q = 0.707f,
        bool enabled = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequencyHz, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(q, 0f);
        Type = type;
        FrequencyHz = frequencyHz;
        GainDb = gainDb;
        Q = q;
        Enabled = enabled;
    }

    /// <summary>Filter shape.</summary>
    public EqualizerBandType Type { get; }

    /// <summary>Center or shelf corner frequency in Hz.</summary>
    public float FrequencyHz { get; }

    /// <summary>Gain in decibels (0 = unity for the band).</summary>
    public float GainDb { get; }

    /// <summary>Filter Q / bandwidth control.</summary>
    public float Q { get; }

    /// <summary>When false, the band contributes unity (0 dB) until re-enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Creates a peaking band.</summary>
    public static EqualizerBandConfig Peaking(float frequencyHz, float gainDb = 0f, float q = 0.707f)
        => new(EqualizerBandType.Peaking, frequencyHz, gainDb, q);

    /// <summary>Creates a low-shelf band.</summary>
    public static EqualizerBandConfig LowShelf(float frequencyHz, float gainDb = 0f, float q = 0.707f)
        => new(EqualizerBandType.LowShelf, frequencyHz, gainDb, q);

    /// <summary>Creates a high-shelf band.</summary>
    public static EqualizerBandConfig HighShelf(float frequencyHz, float gainDb = 0f, float q = 0.707f)
        => new(EqualizerBandType.HighShelf, frequencyHz, gainDb, q);
}
