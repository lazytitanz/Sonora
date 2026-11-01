namespace Sonora.Audio.DSP;

/// <summary>Named equalizer preset (in-memory; applications own persistence).</summary>
public sealed class EqualizerPreset
{
    /// <summary>Creates a preset.</summary>
    public EqualizerPreset(string name, IReadOnlyList<EqualizerBandConfig> bands, float? preampGainDb = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count == 0)
        {
            throw new ArgumentException("At least one band is required.", nameof(bands));
        }

        Name = name;
        Bands = bands.ToArray();
        PreampGainDb = preampGainDb;
    }

    /// <summary>Preset display name.</summary>
    public string Name { get; }

    /// <summary>Band configurations.</summary>
    public IReadOnlyList<EqualizerBandConfig> Bands { get; }

    /// <summary>Optional preamp gain in dB associated with the preset.</summary>
    public float? PreampGainDb { get; }
}
