namespace Sonora.Audio.DSP;

/// <summary>Options for the peak limiter used as clipping protection.</summary>
public sealed class LimiterOptions
{
    /// <summary>
    /// Creates limiter options.
    /// </summary>
    /// <param name="ceilingDb">Output ceiling in dBFS. Default 0 dBFS.</param>
    /// <param name="releaseMs">Release time in milliseconds.</param>
    /// <param name="lookaheadMs">
    /// Lookahead in milliseconds. Default 0 to avoid added latency in 0.3.0.
    /// Non-zero lookahead improves true limiting but delays the stream.
    /// </param>
    public LimiterOptions(float ceilingDb = 0f, float releaseMs = 100f, float lookaheadMs = 0f)
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

    /// <summary>Lookahead in milliseconds (0 = no added latency).</summary>
    public float LookaheadMs { get; }
}
