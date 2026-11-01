namespace Sonora.Audio.DSP;

/// <summary>Options used when creating an equalizer effect.</summary>
public sealed class EqualizerOptions
{
    /// <summary>Creates options with the given bands (must contain at least one band).</summary>
    public EqualizerOptions(IReadOnlyList<EqualizerBandConfig> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count == 0)
        {
            throw new ArgumentException("At least one equalizer band is required.", nameof(bands));
        }

        Bands = bands.ToArray();
    }

    /// <summary>Band layout and initial gains.</summary>
    public IReadOnlyList<EqualizerBandConfig> Bands { get; }

    /// <summary>
    /// Convenient six-band layout (low shelf, four peaking bands, high shelf)
    /// suitable for music-player style equalizers.
    /// </summary>
    public static EqualizerOptions CreateSixBand()
        => new(
        [
            EqualizerBandConfig.LowShelf(60f),
            EqualizerBandConfig.Peaking(150f),
            EqualizerBandConfig.Peaking(400f),
            EqualizerBandConfig.Peaking(1000f),
            EqualizerBandConfig.Peaking(2400f),
            EqualizerBandConfig.HighShelf(15000f),
        ]);
}
