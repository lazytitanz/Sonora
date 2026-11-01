using Sonora.Audio.Buffers;

namespace Sonora.Audio.Tests;

public sealed class PcmRingBufferTests
{
    [Fact]
    public void WriteAndRead_RoundTripsSamples()
    {
        var ring = new PcmRingBuffer(8);
        var written = new float[] { 1, 2, 3, 4, 5 };
        Assert.Equal(5, ring.Write(written, CancellationToken.None));

        var read = new float[8];
        var count = ring.TryRead(read);
        Assert.Equal(5, count);
        Assert.Equal(written, read.AsSpan(0, 5).ToArray());
    }

    [Fact]
    public void TryRead_WhenEmpty_ReturnsZero()
    {
        var ring = new PcmRingBuffer(4);
        Assert.Equal(0, ring.TryRead(new float[2]));
    }

    [Fact]
    public void MarkCompleted_WhenDrained_IsCompleted()
    {
        var ring = new PcmRingBuffer(4);
        ring.Write(new float[] { 1, 2 }, CancellationToken.None);
        ring.MarkCompleted();
        Assert.False(ring.IsCompleted);
        ring.TryRead(new float[2]);
        Assert.True(ring.IsCompleted);
    }
}
