using Sonora.Audio.Playback;

namespace Sonora.Audio.Tests;

public sealed class PcmVolumeTests
{
    [Fact]
    public void Apply_leaves_unity_gain_unchanged()
    {
        var samples = new float[] { 0.25f, -0.5f, 1f };
        var copy = (float[])samples.Clone();
        PcmVolume.Apply(samples, 1f);
        Assert.Equal(copy, samples);
    }

    [Fact]
    public void Apply_scales_samples_by_linear_volume()
    {
        var samples = new float[] { 1f, -0.5f, 0.25f };
        PcmVolume.Apply(samples, 0.5f);
        Assert.Equal(new[] { 0.5f, -0.25f, 0.125f }, samples);
    }

    [Fact]
    public void Apply_zero_clears_buffer()
    {
        var samples = new float[] { 0.9f, -0.9f };
        PcmVolume.Apply(samples, 0f);
        Assert.Equal(new[] { 0f, 0f }, samples);
    }

    [Fact]
    public void Apply_empty_span_is_noop()
    {
        PcmVolume.Apply(Span<float>.Empty, 0.25f);
    }
}
