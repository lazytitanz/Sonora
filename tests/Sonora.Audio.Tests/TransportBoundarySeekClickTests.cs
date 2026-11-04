using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;

namespace Sonora.Audio.Tests;

/// <summary>
/// Regression: seek-style effects-barrier transitions must fade via the transport envelope
/// rather than exposing hard PCM discontinuities or stale pre-seek samples.
/// </summary>
public sealed class TransportBoundarySeekClickTests
{
    private static readonly AudioFormat Mono = AudioFormat.CreateFloat32(44100, 1);
    private static readonly AudioFormat Stereo = AudioFormat.CreateFloat32(44100, 2);
    private const float Amplitude = 0.5f;
    /// <summary>Hard A→0 steps are ~Amplitude; a smooth 8 ms ramp stays well below this.</summary>
    private const float MaxAllowedAdjacentJump = 0.05f;

    [Fact]
    public void SeekBoundary_FadeOutBarrierFadeIn_NoHardDiscontinuity()
    {
        var sampleSource = CreateSource(Mono, Amplitude, sampleCount: 32_768, out var active);
        var period = new float[128];
        var capture = new List<float>(8192);

        Drain(sampleSource, period, capture, blocks: 8);
        Assert.Contains(capture, s => Math.Abs(s - Amplitude) < 1e-3f);

        // Playing-seek sequence: fade out → barrier silence → fade in.
        sampleSource.Transport.RequestFadeOut();
        var fadeOutStart = capture.Count;
        DrainUntil(
            sampleSource,
            period,
            capture,
            predicate: () => sampleSource.Transport.CurrentGain <= 1e-4f,
            maxBlocks: 64);
        Assert.True(sampleSource.Transport.CurrentGain <= 1e-4f);

        sampleSource.ArmEffectsBarrier();
        var barrierStart = capture.Count;
        for (var i = 0; i < 4; i++)
        {
            var n = sampleSource.Read(period);
            Assert.Equal(period.Length, n);
            capture.AddRange(period);
            Assert.All(period, s => Assert.Equal(0f, s));
        }

        sampleSource.ExitEffectsBarrier();
        sampleSource.Transport.RequestFadeIn();
        var fadeInStart = capture.Count;
        DrainUntil(
            sampleSource,
            period,
            capture,
            predicate: () => sampleSource.Transport.CurrentGain >= 1f - 1e-4f,
            maxBlocks: 64);

        Assert.True(
            MaxAdjacentJump(capture, fadeOutStart, barrierStart) < MaxAllowedAdjacentJump,
            $"Fade-out jump too large: {MaxAdjacentJump(capture, fadeOutStart, barrierStart):G6}");
        Assert.True(
            MaxAdjacentJump(capture, fadeInStart, capture.Count) < MaxAllowedAdjacentJump,
            $"Fade-in jump too large: {MaxAdjacentJump(capture, fadeInStart, capture.Count):G6}");
        // First post-barrier sample must start near silence (no abrupt onset).
        Assert.True(Math.Abs(capture[fadeInStart]) < MaxAllowedAdjacentJump);

        active.Dispose();
    }

    [Fact]
    public void ArmEffectsBarrier_SnapsMute_ExcludesStalePcmAfterExitUntilFadeIn()
    {
        var sampleSource = CreateSource(Mono, Amplitude, sampleCount: 16_384, out var active);
        var period = new float[64];
        Drain(sampleSource, period, capture: null, blocks: 4);

        sampleSource.ArmEffectsBarrier();
        Assert.Equal(0f, sampleSource.Transport.CurrentGain);

        // Exit without RequestFadeIn — residual mute must hold (stale PCM exclusion).
        sampleSource.ExitEffectsBarrier();
        var n = sampleSource.Read(period);
        Assert.Equal(period.Length, n);
        Assert.All(period, s => Assert.Equal(0f, s));

        active.Dispose();
    }

    [Fact]
    public void TransportEnvelope_Stereo_ChannelsRemainCoherent()
    {
        using var rack = new AudioEffectRack(null);
        rack.Configure(Stereo);
        rack.Bypass = true;

        var samples = new float[8_192];
        for (var i = 0; i < samples.Length; i += 2)
        {
            samples[i] = Amplitude;
            samples[i + 1] = Amplitude * 0.5f;
        }

        var active = TrackDecodePipeline.Create(
            "stereo.wav",
            new Fakes.SequenceFakeDecoder(Stereo, samples),
            new AudioEngineOptions
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                PrimeDuration = TimeSpan.FromMilliseconds(20),
            });
        active.StartPump(
            processors: Array.Empty<IAudioProcessor>(),
            onSamplesWritten: _ => { },
            onError: null);
        active.Prime(TimeSpan.FromMilliseconds(20));

        var sampleSource = new TransitionSampleSource(
            Stereo,
            rack,
            getVolume: () => 1f,
            onSamplesConsumed: _ => { },
            onUnderrun: () => { },
            isPlaying: () => true,
            getActive: () => active,
            tryPromoteNext: () => null,
            hasPendingNext: () => false,
            scheduleTransitioned: _ => { });

        var period = new float[256];
        sampleSource.Transport.RequestFadeOut();
        for (var i = 0; i < 32; i++)
        {
            var n = sampleSource.Read(period);
            Assert.Equal(period.Length, n);
            for (var f = 0; f < n; f += 2)
            {
                // Identical frame gain ⇒ L/R ratio preserved (0.5 here).
                if (Math.Abs(period[f]) > 1e-6f)
                {
                    Assert.Equal(0.5f, period[f + 1] / period[f], precision: 4);
                }
                else
                {
                    Assert.Equal(0f, period[f + 1]);
                }
            }
        }

        active.Dispose();
    }

    [Fact]
    public void RapidFadeOutFadeIn_DoesNotStickMuted()
    {
        var sampleSource = CreateSource(Mono, Amplitude, sampleCount: 32_768, out var active);
        var period = new float[128];

        for (var round = 0; round < 8; round++)
        {
            sampleSource.Transport.RequestFadeOut();
            DrainUntil(
                sampleSource,
                period,
                capture: null,
                predicate: () => sampleSource.Transport.IsAtTarget,
                maxBlocks: 32);
            sampleSource.Transport.RequestFadeIn();
            DrainUntil(
                sampleSource,
                period,
                capture: null,
                predicate: () => sampleSource.Transport.IsAtTarget,
                maxBlocks: 32);
        }

        Assert.True(sampleSource.Transport.CurrentGain >= 1f - 1e-4f);
        var n = sampleSource.Read(period);
        Assert.Contains(period.AsSpan(0, n).ToArray(), s => Math.Abs(s - Amplitude) < 1e-3f);

        active.Dispose();
    }

    private static TransitionSampleSource CreateSource(
        AudioFormat format,
        float amplitude,
        int sampleCount,
        out TrackDecodePipeline active)
    {
        var rack = new AudioEffectRack(null);
        rack.Configure(format);
        rack.Bypass = true;

        var samples = Enumerable.Repeat(amplitude, sampleCount).ToArray();
        active = TrackDecodePipeline.Create(
            "dc.wav",
            new Fakes.SequenceFakeDecoder(format, samples),
            new AudioEngineOptions
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                PrimeDuration = TimeSpan.FromMilliseconds(20),
            });
        active.StartPump(
            processors: Array.Empty<IAudioProcessor>(),
            onSamplesWritten: _ => { },
            onError: null);
        active.Prime(TimeSpan.FromMilliseconds(20));

        var pipeline = active;
        return new TransitionSampleSource(
            format,
            rack,
            getVolume: () => 1f,
            onSamplesConsumed: _ => { },
            onUnderrun: () => { },
            isPlaying: () => true,
            getActive: () => pipeline,
            tryPromoteNext: () => null,
            hasPendingNext: () => false,
            scheduleTransitioned: _ => { });
    }

    private static void Drain(
        TransitionSampleSource source,
        float[] period,
        List<float>? capture,
        int blocks)
    {
        for (var i = 0; i < blocks; i++)
        {
            var n = source.Read(period);
            Assert.Equal(period.Length, n);
            capture?.AddRange(period);
        }
    }

    private static void DrainUntil(
        TransitionSampleSource source,
        float[] period,
        List<float>? capture,
        Func<bool> predicate,
        int maxBlocks)
    {
        for (var i = 0; i < maxBlocks && !predicate(); i++)
        {
            var n = source.Read(period);
            Assert.Equal(period.Length, n);
            capture?.AddRange(period);
        }
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
}
