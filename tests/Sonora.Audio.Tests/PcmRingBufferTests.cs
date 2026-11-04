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
        Assert.True(ring.IsProducerCompleted);
        ring.TryRead(new float[2]);
        Assert.True(ring.IsCompleted);
        Assert.True(ring.IsProducerCompleted);
    }

    [Fact]
    public void IsProducerCompleted_BeforeMark_IsFalse()
    {
        var ring = new PcmRingBuffer(4);
        ring.Write(new float[] { 1, 2 }, CancellationToken.None);
        Assert.False(ring.IsProducerCompleted);
        Assert.False(ring.IsCompleted);
    }

    [Fact]
    public void Clear_ResetsProducerCompleted()
    {
        var ring = new PcmRingBuffer(4);
        ring.Write(new float[] { 1 }, CancellationToken.None);
        ring.MarkCompleted();
        Assert.True(ring.IsProducerCompleted);
        ring.Clear();
        Assert.False(ring.IsProducerCompleted);
        Assert.Equal(0, ring.Count);
    }

    /// <summary>
    /// Documents a real contract gap: <see cref="PcmRingBuffer"/> does not enforce
    /// channel-frame alignment. A stereo promote that drained an odd leftover sample
    /// before swapping rings would permanently swap L/R for the rest of B.
    /// This test proves the API permits it; it does not prove production decoders do it.
    /// </summary>
    [Fact]
    public void TryRead_DoesNotEnforceFrameAlignment_OddDestinationLength()
    {
        var ring = new PcmRingBuffer(16);
        // Stereo frame = 2 samples; write two full frames then one orphan sample.
        Assert.Equal(5, ring.Write([0.1f, -0.1f, 0.2f, -0.2f, 0.3f], CancellationToken.None));

        var odd = new float[3];
        Assert.Equal(3, ring.TryRead(odd));
        Assert.Equal(0.1f, odd[0]);
        Assert.Equal(-0.1f, odd[1]);
        Assert.Equal(0.2f, odd[2]); // half-frame: next TryRead would start mid-frame

        var rest = new float[8];
        Assert.Equal(2, ring.TryRead(rest));
        Assert.Equal(-0.2f, rest[0]);
        Assert.Equal(0.3f, rest[1]);
    }
}
