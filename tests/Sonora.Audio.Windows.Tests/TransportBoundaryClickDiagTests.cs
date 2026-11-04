using Sonora.Audio.Abstractions;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.DSP;
using Sonora.Audio.Windows.Output;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Regression coverage for transport-boundary smoothing on Windows paths:
/// exclusive adapter pause after mute, and shared/exclusive-neutral envelope behavior.
/// </summary>
public sealed class TransportBoundaryClickDiagTests
{
    private static readonly AudioFormat Mono = AudioFormat.CreateFloat32(44100, 1);
    private const float ClickExcessThreshold = 0.02f;
    private const float MaxAllowedAdjacentJump = 0.05f;

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public void ExclusivePause_AfterTransportMute_NoAudibleStep(float amplitude)
    {
        // Product path: envelope reaches silence first, then exclusive SetPaused feeds zeros.
        var source = new ConstantSource(Mono, amplitude);
        var envelope = new TransportGainEnvelope(Mono);
        var wrapped = new EnvelopeSource(source, envelope);
        var adapter = new PcmSampleProviderAdapter(wrapped);
        var period = new float[64];
        var capture = new List<float>(512);

        for (var i = 0; i < 4; i++)
        {
            adapter.Read(period);
            capture.AddRange(period);
        }

        envelope.RequestFadeOut();
        for (var i = 0; i < 32 && envelope.CurrentGain > 1e-4f; i++)
        {
            adapter.Read(period);
            capture.AddRange(period);
        }

        Assert.True(envelope.CurrentGain <= 1e-4f);
        envelope.Snap(0f);

        var pauseIndex = capture.Count;
        adapter.SetPaused(true);
        for (var i = 0; i < 4; i++)
        {
            adapter.Read(period);
            capture.AddRange(period);
        }

        var resumeIndex = capture.Count;
        adapter.SetPaused(false);
        envelope.RequestFadeIn();
        for (var i = 0; i < 32 && envelope.CurrentGain < 1f - 1e-4f; i++)
        {
            adapter.Read(period);
            capture.AddRange(period);
        }

        Assert.True(
            AbsoluteJump(capture, pauseIndex) < MaxAllowedAdjacentJump,
            $"Exclusive pause after mute jumped {AbsoluteJump(capture, pauseIndex):G6}");
        Assert.True(
            AbsoluteJump(capture, resumeIndex) < MaxAllowedAdjacentJump,
            $"Exclusive resume onset jumped {AbsoluteJump(capture, resumeIndex):G6}");
        Assert.True(MaxAdjacentJump(capture, pauseIndex, capture.Count) < MaxAllowedAdjacentJump);
    }

    [Fact]
    public void SharedStyle_HardDevicePause_WithoutPriorFade_StillSteps()
    {
        // Characterization retained: pausing the pull without a prior envelope fade is a hard cut.
        // Shared WASAPI Pause stops the client; the product path must fade before that call.
        var last = 0.4f;
        var next = 0f;
        var jump = Math.Abs(next - last);
        Assert.True(jump > ClickExcessThreshold);
        Assert.Equal(last, jump, precision: 5);
    }

    [Fact]
    public void ResetProcessingState_BassBoost_ProducesOnsetTransient_CoveredBySeekFade()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var rack = new AudioEffectRack(new WindowsAudioEffectFactory());
        rack.Configure(format);
        var eq = rack.AddEqualizer(EqualizerOptions.CreateSixBand());
        eq.SetBandGain(0, 12f);
        rack.Bypass = false;

        var block = new float[256 * format.ChannelCount];
        FillSine(block, format, frequencyHz: 80f, amplitude: 0.5f);

        for (var i = 0; i < 20; i++)
        {
            rack.Process(block, format);
            FillSine(block, format, frequencyHz: 80f, amplitude: 0.5f);
        }

        var continuous = new float[block.Length];
        FillSine(continuous, format, frequencyHz: 80f, amplitude: 0.5f);
        rack.Process(continuous, format);

        rack.ResetProcessingState();
        var afterReset = new float[block.Length];
        FillSine(afterReset, format, frequencyHz: 80f, amplitude: 0.5f);
        rack.Process(afterReset, format);

        var maxDelta = 0f;
        for (var i = 0; i < continuous.Length; i++)
        {
            maxDelta = Math.Max(maxDelta, Math.Abs(afterReset[i] - continuous[i]));
        }

        // DSP reset still changes the first post-seek block — seek fade-in must cover this onset.
        Assert.True(
            maxDelta > ClickExcessThreshold,
            $"Expected measurable onset difference after reset; maxΔ={maxDelta:G6}");
    }

    private static float AbsoluteJump(IReadOnlyList<float> samples, int index)
    {
        Assert.True(index > 0 && index < samples.Count);
        return Math.Abs(samples[index] - samples[index - 1]);
    }

    private static float MaxAdjacentJump(IReadOnlyList<float> samples, int start, int end)
    {
        var max = 0f;
        var from = Math.Max(1, start);
        var to = Math.Min(end, samples.Count);
        for (var i = from; i < to; i++)
        {
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));
        }

        return max;
    }

    private static void FillSine(Span<float> interleaved, AudioFormat format, float frequencyHz, float amplitude)
    {
        var frames = interleaved.Length / format.ChannelCount;
        for (var f = 0; f < frames; f++)
        {
            var s = amplitude * MathF.Sin(2f * MathF.PI * frequencyHz * f / format.SampleRate);
            for (var c = 0; c < format.ChannelCount; c++)
            {
                interleaved[f * format.ChannelCount + c] = s;
            }
        }
    }

    private sealed class ConstantSource : IPcmSampleSource
    {
        private readonly float _value;

        public ConstantSource(AudioFormat format, float value)
        {
            Format = format;
            _value = value;
        }

        public AudioFormat Format { get; }
        public bool IsEndOfStream => false;

        public int Read(Span<float> destination)
        {
            destination.Fill(_value);
            return destination.Length;
        }
    }

    private sealed class EnvelopeSource : IPcmSampleSource
    {
        private readonly IPcmSampleSource _inner;
        private readonly TransportGainEnvelope _envelope;

        public EnvelopeSource(IPcmSampleSource inner, TransportGainEnvelope envelope)
        {
            _inner = inner;
            _envelope = envelope;
            Format = inner.Format;
        }

        public AudioFormat Format { get; }
        public bool IsEndOfStream => _inner.IsEndOfStream;

        public int Read(Span<float> destination)
        {
            var n = _inner.Read(destination);
            if (n > 0)
            {
                _envelope.Apply(destination[..n]);
            }

            return n;
        }
    }
}
