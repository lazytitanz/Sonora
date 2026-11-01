namespace Sonora.Audio.DSP;

/// <summary>Built-in equalizer presets for the default six-band layout.</summary>
public static class EqualizerPresets
{
    /// <summary>All bands at 0 dB.</summary>
    public static EqualizerPreset Flat { get; } = SixBand("Flat", [0, 0, 0, 0, 0, 0]);

    /// <summary>Boosted low end.</summary>
    public static EqualizerPreset BassBoost { get; } = SixBand("Bass Boost", [6, 4, 1, 0, 0, 0]);

    /// <summary>Boosted high end.</summary>
    public static EqualizerPreset TrebleBoost { get; } = SixBand("Treble Boost", [0, 0, 0, 1, 3, 5]);

    /// <summary>Mild mid emphasis for vocals.</summary>
    public static EqualizerPreset Vocal { get; } = SixBand("Vocal", [-2, -1, 2, 4, 2, 0]);

    /// <summary>All built-in presets.</summary>
    public static IReadOnlyList<EqualizerPreset> All { get; } = [Flat, BassBoost, TrebleBoost, Vocal];

    private static EqualizerPreset SixBand(string name, float[] gainsDb)
    {
        var template = EqualizerOptions.CreateSixBand().Bands;
        var bands = new EqualizerBandConfig[template.Count];
        for (var i = 0; i < template.Count; i++)
        {
            var t = template[i];
            bands[i] = new EqualizerBandConfig(t.Type, t.FrequencyHz, gainsDb[i], t.Q, t.Enabled);
        }

        return new EqualizerPreset(name, bands);
    }
}
