using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Windows.Output;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Unit coverage for the render-submission probe (no hardware). Proves the probe observes
/// the same float bit pattern SampleToWaveProvider writes into the lease byte buffer.
/// </summary>
public sealed class WasapiRenderSubmissionProbeTests
{
    private static readonly AudioFormat Stereo = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public void Read_PassThrough_RecordsJournal_AndCapturesLeaseFloats()
    {
        var source = new SequenceSource(Stereo, [0.25f, -0.75f, 0.80f, -0.40f]);
        var adapter = new PcmSampleProviderAdapter(source);
        var probe = new WasapiRenderSubmissionProbe(new SampleToWaveProvider(adapter));
        probe.ArmCapture(skipSamples: 0, captureSamples: 4);

        Span<byte> lease = stackalloc byte[4 * sizeof(float)];
        var bytes = probe.Read(lease);
        Assert.Equal(16, bytes);

        Assert.True(probe.Capture!.IsComplete);
        Span<float> captured = stackalloc float[4];
        Assert.True(probe.Capture.TryCopyCaptured(captured, out var written));
        Assert.Equal(4, written);
        Assert.Equal(0.25f, captured[0]);
        Assert.Equal(-0.75f, captured[1]);
        Assert.Equal(0.80f, captured[2]);
        Assert.Equal(-0.40f, captured[3]);

        // Lease bytes are the IEEE float bit pattern (zero-copy cast target).
        var leaseFloats = MemoryMarshal.Cast<byte, float>(lease);
        Assert.Equal(captured[0], leaseFloats[0]);
        Assert.Equal(captured[1], leaseFloats[1]);

        var journal = probe.Journal.Snapshot();
        Assert.Single(journal);
        Assert.Equal(16, journal[0].BytesRequested);
        Assert.Equal(16, journal[0].BytesReturned);
        Assert.Equal(2, journal[0].FramesRequested);
        Assert.Equal(2, journal[0].FramesReturned);
        Assert.False(journal[0].ShortRead);
        Assert.False(journal[0].OddInterleavedFloatCount);
    }

    [Fact]
    public void Disarmed_Read_DoesNotCapture_StillPassThrough()
    {
        var source = new SequenceSource(Stereo, [0.1f, -0.1f]);
        var adapter = new PcmSampleProviderAdapter(source);
        var probe = new WasapiRenderSubmissionProbe(new SampleToWaveProvider(adapter));

        Span<byte> lease = stackalloc byte[8];
        Assert.Equal(8, probe.Read(lease));
        Assert.Null(probe.Capture);
        Assert.Single(probe.Journal.Snapshot());
    }

    private sealed class SequenceSource : IPcmSampleSource
    {
        private readonly float[] _samples;
        private int _pos;

        public SequenceSource(AudioFormat format, float[] samples)
        {
            Format = format;
            _samples = samples;
        }

        public AudioFormat Format { get; }
        public bool IsEndOfStream => _pos >= _samples.Length;

        public int Read(Span<float> destination)
        {
            var n = Math.Min(destination.Length, _samples.Length - _pos);
            if (n <= 0)
            {
                return 0;
            }

            _samples.AsSpan(_pos, n).CopyTo(destination);
            _pos += n;
            return n;
        }
    }
}
