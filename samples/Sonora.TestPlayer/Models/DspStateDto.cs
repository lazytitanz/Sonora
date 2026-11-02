namespace Sonora.TestPlayer.Models;

/// <summary>State of one equalizer band.</summary>
/// <param name="Index">Zero-based band index.</param>
/// <param name="FrequencyHz">Center or shelf corner frequency.</param>
/// <param name="GainDb">Current gain in dB.</param>
/// <param name="Enabled">Whether the band contributes (disabled bands act as 0 dB).</param>
public sealed record EqBandDto(int Index, float FrequencyHz, float GainDb, bool Enabled);

/// <summary>
/// Snapshot of the DSP rack configured by <c>DspController</c>: Preamp → Equalizer (six-band) → Limiter.
/// </summary>
public sealed record DspStateDto
{
    /// <summary>Rack-wide bypass; when true every effect is skipped.</summary>
    public bool RackBypass { get; init; }

    /// <summary>Whether the preamp is enabled.</summary>
    public bool PreampEnabled { get; init; }

    /// <summary>Preamp gain in dB.</summary>
    public float PreampGainDb { get; init; }

    /// <summary>Whether the equalizer is enabled.</summary>
    public bool EqualizerEnabled { get; init; }

    /// <summary>Per-band state.</summary>
    public IReadOnlyList<EqBandDto> Bands { get; init; } = [];

    /// <summary>Name of the last preset applied via the controller, or <see langword="null"/> after manual edits.</summary>
    public string? ActivePreset { get; init; }

    /// <summary>Names of the built-in presets.</summary>
    public IReadOnlyList<string> PresetNames { get; init; } = [];

    /// <summary>Whether the limiter is enabled.</summary>
    public bool LimiterEnabled { get; init; }

    /// <summary>Limiter ceiling in dBFS.</summary>
    public float LimiterCeilingDb { get; init; }

    /// <summary>Limiter release time in milliseconds.</summary>
    public float LimiterReleaseMs { get; init; }

    /// <summary>
    /// Limiter look-ahead in milliseconds. This is fixed when the limiter is constructed and cannot be
    /// changed at runtime; changing it would require recreating the effect.
    /// </summary>
    public float LimiterLookaheadMs { get; init; }

    /// <summary>Always <see langword="true"/>: look-ahead is a construction-time-only setting.</summary>
    public bool LimiterLookaheadIsConstructionTime { get; init; } = true;
}
