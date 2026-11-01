namespace Sonora.Audio.DSP;

/// <summary>
/// Peak limiter used as conservative clipping protection.
/// </summary>
/// <remarks>
/// A limiter is not a guarantee of distortion-free output under extreme boosts.
/// Zero-lookahead limiting can still allow inter-sample overs; see documentation.
/// </remarks>
public interface ILimiterEffect : IAudioEffect
{
    /// <summary>Output ceiling in dBFS.</summary>
    float CeilingDb { get; set; }

    /// <summary>Release time in milliseconds.</summary>
    float ReleaseMs { get; set; }
}
