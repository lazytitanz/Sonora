namespace Sonora.Audio.DSP;

/// <summary>Options for the sample-peak limiter used as clipping protection.</summary>
/// <remarks>
/// <para>
/// Sonora's limiter is a <strong>sample-peak</strong> look-ahead brick-wall (not true-peak / inter-sample).
/// Stereo/multi-channel peaks are fully linked (one gain for all channels). Soft clipping is not applied.
/// </para>
/// <para>
/// Measured with NAudio 3.3.0 (<c>TruePeak=false</c>): <see cref="LookaheadMs"/> of 0 still uses at least
/// one sample of internal delay and held the sample-peak ceiling for impulses, mid-buffer steps, and
/// sparse bursts in Sonora's tests. Default <see cref="DefaultGuaranteeingLookaheadMs"/> adds explicit
/// multi-sample look-ahead as the guaranteeing construction setting. True-peak / inter-sample peaks are
/// never claimed. Soft clipping is not applied.
/// </para>
/// </remarks>
public sealed class LimiterOptions
{
    /// <summary>
    /// Default look-ahead that guarantees the sample-peak ceiling for abrupt steps in Sonora's tests (ms).
    /// </summary>
    public const float DefaultGuaranteeingLookaheadMs = 5f;

    /// <summary>
    /// Creates limiter options.
    /// </summary>
    /// <param name="ceilingDb">Output ceiling in dBFS. Default 0 dBFS.</param>
    /// <param name="releaseMs">Release time in milliseconds.</param>
    /// <param name="lookaheadMs">
    /// Lookahead in milliseconds. Default <see cref="DefaultGuaranteeingLookaheadMs"/> so AddLimiter()
    /// guarantees the sample-peak ceiling. Pass <c>0</c> explicitly for minimum latency (may overshoot).
    /// Even <c>0</c> incurs at least one sample of internal delay in the Windows backend.
    /// </param>
    public LimiterOptions(
        float ceilingDb = 0f,
        float releaseMs = 100f,
        float lookaheadMs = DefaultGuaranteeingLookaheadMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(releaseMs, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookaheadMs, 0f);
        CeilingDb = ceilingDb;
        ReleaseMs = releaseMs;
        LookaheadMs = lookaheadMs;
    }

    /// <summary>Output ceiling in dBFS.</summary>
    public float CeilingDb { get; }

    /// <summary>Release time in milliseconds.</summary>
    public float ReleaseMs { get; }

    /// <summary>
    /// Lookahead in milliseconds. Values &gt;= 1 guarantee the sample-peak ceiling for tested abrupt steps;
    /// 0 minimizes latency but may overshoot. Always at least one sample of internal delay on Windows.
    /// </summary>
    public float LookaheadMs { get; }
}
