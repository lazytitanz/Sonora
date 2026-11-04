namespace Sonora.Audio.Playback;

/// <summary>
/// Allocation-free crossfade gain helpers and in-place PCM mixing for the transition pull path.
/// </summary>
internal static class CrossfadeMixer
{
    /// <summary>
    /// Computes outgoing (A) and incoming (B) gains for progress <paramref name="t"/> in [0, 1].
    /// Values outside the range are clamped.
    /// </summary>
    public static void Gains(CrossfadeCurve curve, float t, out float gainA, out float gainB)
    {
        if (t <= 0f)
        {
            gainA = 1f;
            gainB = 0f;
            return;
        }

        if (t >= 1f)
        {
            gainA = 0f;
            gainB = 1f;
            return;
        }

        switch (curve)
        {
            case CrossfadeCurve.Linear:
                gainA = 1f - t;
                gainB = t;
                break;
            case CrossfadeCurve.EqualPower:
            default:
                var angle = t * (MathF.PI * 0.5f);
                gainA = MathF.Cos(angle);
                gainB = MathF.Sin(angle);
                break;
        }
    }

    /// <summary>
    /// Mixes interleaved float PCM: <c>destination[i] = gainA * a[i] + gainB * b[i]</c>.
    /// All spans must share the same length.
    /// </summary>
    public static void Mix(
        ReadOnlySpan<float> a,
        ReadOnlySpan<float> b,
        Span<float> destination,
        float gainA,
        float gainB)
    {
        if (a.Length != b.Length || a.Length != destination.Length)
        {
            throw new ArgumentException("Crossfade mix spans must have equal length.");
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (gainA * a[i]) + (gainB * b[i]);
        }
    }

    /// <summary>
    /// Applies a constant gain to <paramref name="destination"/> in place (used when the other
    /// track contributes silence for a frame group).
    /// </summary>
    public static void ScaleInPlace(Span<float> destination, float gain)
    {
        if (MathF.Abs(gain - 1f) < 0.0001f)
        {
            return;
        }

        if (gain <= 0f)
        {
            destination.Clear();
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] *= gain;
        }
    }

    /// <summary>
    /// Converts a crossfade duration to a whole-frame count at the session sample rate.
    /// </summary>
    public static int DurationToFadeFrames(TimeSpan duration, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        var frames = (long)Math.Ceiling(duration.TotalSeconds * sampleRate);
        if (frames > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)frames;
    }
}
