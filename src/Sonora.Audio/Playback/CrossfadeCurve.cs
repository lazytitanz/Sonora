namespace Sonora.Audio.Playback;

/// <summary>
/// Gain envelope used when <see cref="AudioPlayer.CrossfadeDuration"/> is greater than zero.
/// </summary>
public enum CrossfadeCurve
{
    /// <summary>Outgoing gain <c>1-t</c>, incoming gain <c>t</c>.</summary>
    Linear = 0,

    /// <summary>
    /// Outgoing gain <c>cos(t·π/2)</c>, incoming gain <c>sin(t·π/2)</c>.
    /// Preferred default for uncorrelated music material.
    /// </summary>
    EqualPower = 1,
}
