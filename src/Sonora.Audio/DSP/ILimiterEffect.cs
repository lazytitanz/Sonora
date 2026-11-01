namespace Sonora.Audio.DSP;

/// <summary>
/// Sample-peak limiter used as clipping protection (not true-peak / inter-sample limiting).
/// </summary>
/// <remarks>
/// <para>
/// Channels are gain-linked. Soft clipping is not applied. True-peak / oversampled limiting is not used.
/// </para>
/// <para>
/// Ceiling guarantees depend on construction-time <see cref="LimiterOptions.LookaheadMs"/>.
/// The default (<see cref="LimiterOptions.DefaultGuaranteeingLookaheadMs"/>) is the guaranteeing
/// multi-sample look-ahead setting. Explicit <c>LookaheadMs = 0</c> still uses ≥1 sample of backend delay
/// and held the sample-peak ceiling for Sonora's impulse/step/burst tests, but is the minimum-latency
/// setting and is not true-peak safe. Inter-sample peaks between samples are never claimed.
/// </para>
/// <para>
/// Recommend rack order Preamp → Equalizer → Limiter. Dynamics state resets on seek/stop/load/output
/// reconfiguration but not on gapless track joins.
/// </para>
/// </remarks>
public interface ILimiterEffect : IAudioEffect
{
    /// <summary>Output ceiling in dBFS.</summary>
    float CeilingDb { get; set; }

    /// <summary>Release time in milliseconds.</summary>
    float ReleaseMs { get; set; }
}
