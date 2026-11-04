using Sonora.Audio.Playback;

namespace Sonora.Audio.Tests;

public sealed class CrossfadeMixerTests
{
    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(0.25f, 0.75f, 0.25f)]
    [InlineData(0.5f, 0.5f, 0.5f)]
    [InlineData(0.75f, 0.25f, 0.75f)]
    public void LinearGains_MatchExpected(float t, float expectedA, float expectedB)
    {
        CrossfadeMixer.Gains(CrossfadeCurve.Linear, t, out var gainA, out var gainB);
        Assert.Equal(expectedA, gainA, precision: 5);
        Assert.Equal(expectedB, gainB, precision: 5);
    }

    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 0f, 1f)]
    public void EqualPowerGains_Endpoints(float t, float expectedA, float expectedB)
    {
        CrossfadeMixer.Gains(CrossfadeCurve.EqualPower, t, out var gainA, out var gainB);
        Assert.Equal(expectedA, gainA, precision: 5);
        Assert.Equal(expectedB, gainB, precision: 5);
    }

    [Fact]
    public void EqualPowerGains_Midpoint_PreserveUnitEnergy()
    {
        CrossfadeMixer.Gains(CrossfadeCurve.EqualPower, 0.5f, out var gainA, out var gainB);
        Assert.Equal(MathF.Sqrt(0.5f), gainA, precision: 5);
        Assert.Equal(MathF.Sqrt(0.5f), gainB, precision: 5);
        Assert.Equal(1f, (gainA * gainA) + (gainB * gainB), precision: 5);
    }

    [Fact]
    public void Gains_ClampOutsideUnitInterval()
    {
        CrossfadeMixer.Gains(CrossfadeCurve.Linear, -0.5f, out var aLow, out var bLow);
        Assert.Equal(1f, aLow);
        Assert.Equal(0f, bLow);

        CrossfadeMixer.Gains(CrossfadeCurve.EqualPower, 1.5f, out var aHigh, out var bHigh);
        Assert.Equal(0f, aHigh);
        Assert.Equal(1f, bHigh);
    }

    [Fact]
    public void Mix_LinearIdenticalSignals_PreservesAmplitude()
    {
        var a = new float[] { 0.5f, -0.5f, 0.25f, -0.25f };
        var b = (float[])a.Clone();
        var dest = new float[a.Length];

        CrossfadeMixer.Gains(CrossfadeCurve.Linear, 0.5f, out var gA, out var gB);
        CrossfadeMixer.Mix(a, b, dest, gA, gB);

        Assert.Equal(a, dest);
    }

    [Fact]
    public void Mix_EqualPowerUncorrelated_AppliesGainsPerSample()
    {
        var a = new float[] { 1f, 1f, 0f, 0f };
        var b = new float[] { 0f, 0f, 1f, 1f };
        var dest = new float[4];

        CrossfadeMixer.Gains(CrossfadeCurve.EqualPower, 0.5f, out var gA, out var gB);
        CrossfadeMixer.Mix(a, b, dest, gA, gB);

        Assert.Equal(gA, dest[0], precision: 5);
        Assert.Equal(gA, dest[1], precision: 5);
        Assert.Equal(gB, dest[2], precision: 5);
        Assert.Equal(gB, dest[3], precision: 5);
    }

    [Fact]
    public void Mix_MismatchedLengths_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CrossfadeMixer.Mix(new float[2], new float[3], new float[2], 1f, 0f));
    }

    [Fact]
    public void ScaleInPlace_ZeroClears_OneIsNoOp()
    {
        var samples = new float[] { 0.5f, -0.25f };
        CrossfadeMixer.ScaleInPlace(samples, 1f);
        Assert.Equal(new float[] { 0.5f, -0.25f }, samples);

        CrossfadeMixer.ScaleInPlace(samples, 0f);
        Assert.Equal(new float[] { 0f, 0f }, samples);
    }

    [Fact]
    public void DurationToFadeFrames_CeilsAndHandlesZero()
    {
        Assert.Equal(0, CrossfadeMixer.DurationToFadeFrames(TimeSpan.Zero, 44100));
        Assert.Equal(44100, CrossfadeMixer.DurationToFadeFrames(TimeSpan.FromSeconds(1), 44100));
        Assert.Equal(48000, CrossfadeMixer.DurationToFadeFrames(TimeSpan.FromSeconds(1), 48000));
        Assert.Equal(441, CrossfadeMixer.DurationToFadeFrames(TimeSpan.FromMilliseconds(10), 44100));
        Assert.Equal(45, CrossfadeMixer.DurationToFadeFrames(TimeSpan.FromMilliseconds(1), 44100));
    }
}
