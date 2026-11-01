namespace Sonora.Audio.Windows.Tests;

internal static class PcmFrequencyAnalyzer
{
    public static float EstimateDominantFrequency(ReadOnlySpan<float> interleaved, int sampleRate, int channels, params float[] candidates)
    {
        if (candidates.Length == 0)
        {
            candidates = FixtureHelper.SegmentFrequenciesHz;
        }

        var frames = interleaved.Length / Math.Max(1, channels);
        if (frames < 64)
        {
            return 0;
        }

        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                sum += interleaved[i * channels + ch];
            }

            mono[i] = sum / channels;
        }

        // Use a stable interior window to avoid encoder padding / seek ramp edges.
        var start = Math.Min(frames / 8, frames - 64);
        var length = Math.Min(frames - start, sampleRate / 5); // ~200ms max
        if (length < 64)
        {
            length = frames - start;
        }

        var window = mono.AsSpan(start, length);

        float bestFreq = 0;
        double bestPower = -1;
        foreach (var candidate in candidates)
        {
            var power = GoertzelPower(window, sampleRate, candidate);
            if (power > bestPower)
            {
                bestPower = power;
                bestFreq = candidate;
            }
        }

        return bestFreq;
    }

    public static void AssertSegmentFrequency(ReadOnlySpan<float> interleaved, int sampleRate, int channels, float expectedHz, float toleranceHz = 30f)
    {
        var estimated = EstimateDominantFrequency(interleaved, sampleRate, channels, FixtureHelper.SegmentFrequenciesHz);
        Assert.InRange(estimated, expectedHz - toleranceHz, expectedHz + toleranceHz);
    }

    public static float GoertzelMagnitude(ReadOnlySpan<float> samples, int sampleRate, float frequency)
        => (float)Math.Sqrt(GoertzelPower(samples, sampleRate, frequency));

    private static double GoertzelPower(ReadOnlySpan<float> samples, int sampleRate, float frequency)
    {
        var k = (int)(0.5 + samples.Length * frequency / sampleRate);
        var w = 2.0 * Math.PI * k / samples.Length;
        var cosine = Math.Cos(w);
        var coeff = 2.0 * cosine;
        double s0 = 0, s1 = 0, s2 = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            s0 = samples[i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }
}
