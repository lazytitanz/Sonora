using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Windows.DSP;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Measures and locks the sample-peak limiter contract. Numbers here are from real Process runs —
/// not documentation guesses.
/// </summary>
public sealed class LimiterContractTests
{
    private static readonly AudioFormat Mono44k = AudioFormat.CreateFloat32(44100, 1);
    private static readonly AudioFormat Stereo44k = AudioFormat.CreateFloat32(44100, 2);
    private static readonly AudioFormat Mono48k = AudioFormat.CreateFloat32(48000, 1);
    private static readonly AudioFormat Mono96k = AudioFormat.CreateFloat32(96000, 1);

    /// <summary>
    /// Measured: with NAudio LookaheadMs=0 (still ≥1 internal sample), a lone full-scale impulse at index 0
    /// is held to the ceiling. Guaranteeing look-ahead (default) must keep the same bound with margin for float error.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(5f)]
    public void Limiter_Impulse_RespectsCeiling(float lookaheadMs)
    {
        var ceilingDb = -6f;
        var ceiling = DbToLinear(ceilingDb);
        var lim = CreateLimiter(ceilingDb, lookaheadMs);
        var buffer = new float[4096];
        buffer[0] = 1f; // full-scale impulse

        lim.Process(buffer, Mono44k);
        var peak = Peak(buffer);
        Assert.True(peak <= ceiling * 1.001f, $"LA={lookaheadMs} peak={peak} ceiling={ceiling}");
    }

    /// <summary>
    /// Measured (NAudio 3.3.0, TruePeak=false): LookaheadMs=0 still applies ≥1 sample of delay and held the
    /// sample-peak ceiling for mid-buffer full-scale steps and sparse bursts in these tests. LookaheadMs &gt;= 1
    /// remains the explicitly guaranteeing construction setting; 0 is the minimum-latency setting that relies
    /// on the backend's one-sample delay (not true-peak safe).
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(5f)]
    public void Limiter_MidBufferStep_RespectsSamplePeakCeiling(float lookaheadMs)
    {
        var ceilingDb = -6f;
        var ceiling = DbToLinear(ceilingDb);
        var lim = CreateLimiter(ceilingDb, lookaheadMs);
        var buffer = new float[8192];
        for (var i = 0; i < 4096; i++)
        {
            buffer[i] = 0.05f;
        }

        for (var i = 4096; i < 8192; i++)
        {
            buffer[i] = 1f;
        }

        lim.Process(buffer, Mono44k);
        var peak = Peak(buffer);
        Assert.True(peak <= ceiling * 1.001f, $"LA={lookaheadMs} peak={peak} ceiling={ceiling}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    public void Limiter_SparseBurst_RespectsSamplePeakCeiling(float lookaheadMs)
    {
        var ceilingDb = 0f;
        var lim = CreateLimiter(ceilingDb, lookaheadMs);
        var buffer = new float[4096];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (i % 64) < 4 ? 1f : 0.01f;
        }

        lim.Process(buffer, Mono44k);
        Assert.True(Peak(buffer) <= 1.001f, $"LA={lookaheadMs} peak={Peak(buffer)}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    public void Limiter_SustainedHotSine_ByLookahead(float lookaheadMs)
    {
        var ceilingDb = -6f;
        var ceiling = DbToLinear(ceilingDb);
        var lim = CreateLimiter(ceilingDb, lookaheadMs);
        var buffer = Tone(440f, Mono44k, 0.95f, 8192);
        lim.Process(buffer, Mono44k);
        var peak = Peak(buffer);
        Assert.True(peak <= ceiling * 1.001f, $"LA={lookaheadMs} sustained peak={peak} ceiling={ceiling}");
    }

    [Fact]
    public void Limiter_DefaultOptions_LookaheadIsGuaranteeing()
    {
        // Correctness-first default: look-ahead must be enough that a full-scale impulse stays ≤ ceiling.
        var options = new LimiterOptions();
        Assert.True(options.LookaheadMs > 0f, "Default limiter look-ahead must guarantee sample-peak ceiling.");

        var lim = new NAudioLimiterEffect(options);
        lim.Configure(Mono44k);
        lim.ApplyPendingUpdates();
        var buffer = new float[2048];
        buffer[10] = 1f;
        lim.Process(buffer, Mono44k);
        var ceiling = DbToLinear(options.CeilingDb);
        Assert.True(Peak(buffer) <= ceiling * 1.001f, $"default LA peak={Peak(buffer)} ceiling={ceiling}");
    }

    [Fact]
    public void Limiter_StereoLink_UsesMaxAbsChannel()
    {
        var lim = CreateLimiter(ceilingDb: -6f, lookaheadMs: 5f);
        lim.Configure(Stereo44k);
        lim.ApplyPendingUpdates();

        var buffer = new float[512 * 2];
        for (var i = 0; i < 512; i++)
        {
            buffer[i * 2] = 1f; // left hot
            buffer[i * 2 + 1] = 0.1f; // right quiet
        }

        lim.Process(buffer, Stereo44k);
        var ceiling = DbToLinear(-6f);
        for (var i = 0; i < 512; i++)
        {
            Assert.True(MathF.Abs(buffer[i * 2]) <= ceiling * 1.001f);
            // Linked gain must also attenuate the quiet channel proportionally (not leave it at 0.1 when left hits 1).
            Assert.True(MathF.Abs(buffer[i * 2 + 1]) < 0.1f);
        }
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Limiter_GuaranteeingLookahead_StableAcrossSampleRates(int sampleRate)
    {
        var format = AudioFormat.CreateFloat32(sampleRate, 1);
        var lim = CreateLimiter(-3f, lookaheadMs: 5f);
        lim.Configure(format);
        lim.ApplyPendingUpdates();
        var buffer = Tone(1000f, format, 0.99f, frames: sampleRate / 10);
        lim.Process(buffer, format);
        var ceiling = DbToLinear(-3f);
        Assert.True(Peak(buffer) <= ceiling * 1.001f, $"sr={sampleRate} peak={Peak(buffer)}");
    }

    [Fact]
    public void Limiter_Silence_StaysSilent()
    {
        var lim = CreateLimiter(0f, 5f);
        var buffer = new float[1024];
        lim.Process(buffer, Mono44k);
        Assert.Equal(0f, Peak(buffer));
    }

    [Fact]
    public void Limiter_NaNAndInfinity_DoNotPropagateFromFiniteInput()
    {
        var lim = CreateLimiter(0f, 5f);
        var buffer = Tone(440f, Mono44k, 0.5f, 512);
        lim.Process(buffer, Mono44k);
        Assert.All(buffer, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Limiter_InvalidFloatInput_SanitizedToFinite()
    {
        var lim = CreateLimiter(0f, 5f);
        var buffer = new float[256];
        buffer[0] = float.NaN;
        buffer[1] = float.PositiveInfinity;
        buffer[2] = float.NegativeInfinity;
        lim.Process(buffer, Mono44k);
        Assert.All(buffer, s => Assert.True(float.IsFinite(s), $"non-finite {s}"));
    }

    [Fact]
    public void Limiter_AmplitudeStep_WithGuaranteeingLookahead()
    {
        var lim = CreateLimiter(-6f, 5f);
        var buffer = new float[4096];
        for (var i = 0; i < 2048; i++)
        {
            buffer[i] = 0.1f;
        }

        for (var i = 2048; i < 4096; i++)
        {
            buffer[i] = 0.95f;
        }

        lim.Process(buffer, Mono44k);
        Assert.True(Peak(buffer) <= DbToLinear(-6f) * 1.001f);
    }

    [Fact]
    public void Limiter_ZeroLookaheadOption_StillConstructibleForLegacy()
    {
        // Explicit LA=0 remains available but is documented as non-guaranteeing.
        var lim = CreateLimiter(0f, lookaheadMs: 0f);
        var buffer = new float[128];
        buffer[0] = 1f;
        lim.Process(buffer, Mono44k);
        Assert.True(float.IsFinite(Peak(buffer)));
    }

    private static NAudioLimiterEffect CreateLimiter(float ceilingDb, float lookaheadMs)
    {
        var lim = new NAudioLimiterEffect(new LimiterOptions(ceilingDb, releaseMs: 50f, lookaheadMs: lookaheadMs));
        lim.Configure(Mono44k);
        lim.ApplyPendingUpdates();
        return lim;
    }

    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    private static float Peak(float[] buffer)
    {
        var peak = 0f;
        foreach (var s in buffer)
        {
            peak = Math.Max(peak, MathF.Abs(s));
        }

        return peak;
    }

    private static float[] Tone(float hz, AudioFormat format, float amplitude, int frames)
    {
        var buffer = new float[frames * format.ChannelCount];
        for (var i = 0; i < frames; i++)
        {
            var s = amplitude * MathF.Sin(2 * MathF.PI * hz * i / format.SampleRate);
            for (var ch = 0; ch < format.ChannelCount; ch++)
            {
                buffer[i * format.ChannelCount + ch] = s;
            }
        }

        return buffer;
    }
}
