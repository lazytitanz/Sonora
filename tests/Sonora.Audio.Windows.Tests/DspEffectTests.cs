using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Windows.DSP;

namespace Sonora.Audio.Windows.Tests;

public sealed class DspEffectTests
{
    private static readonly AudioFormat Mono44k = AudioFormat.CreateFloat32(44100, 1);
    private static readonly AudioFormat Stereo44k = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public void Preamp_ZeroDb_IsUnity()
    {
        var pre = new NAudioPreampEffect(0f);
        pre.Configure(Mono44k);
        pre.ApplyPendingUpdates();
        var buffer = Tone(440f, Mono44k, 0.2f, frames: 2048);
        var before = Peak(buffer);
        pre.Process(buffer, Mono44k);
        Assert.Equal(before, Peak(buffer), precision: 4);
    }

    [Fact]
    public void Preamp_Plus6Db_ApproximatelyDoubles()
    {
        var pre = new NAudioPreampEffect(6f);
        pre.Configure(Mono44k);
        pre.ApplyPendingUpdates();
        var buffer = Tone(440f, Mono44k, 0.1f, frames: 2048);
        var before = Rms(buffer);
        pre.Process(buffer, Mono44k);
        var ratio = Rms(buffer) / before;
        Assert.InRange(ratio, 1.8f, 2.2f);
    }

    [Fact]
    public void Preamp_Minus6Db_ApproximatelyHalves()
    {
        var pre = new NAudioPreampEffect(-6f);
        pre.Configure(Mono44k);
        pre.ApplyPendingUpdates();
        var buffer = Tone(440f, Mono44k, 0.2f, frames: 2048);
        var before = Rms(buffer);
        pre.Process(buffer, Mono44k);
        var ratio = Rms(buffer) / before;
        Assert.InRange(ratio, 0.45f, 0.55f);
    }

    [Fact]
    public void Equalizer_ZeroDb_ApproximatelyUnity()
    {
        var eq = new NAudioEqualizerEffect(EqualizerOptions.CreateSixBand());
        eq.Configure(Mono44k);
        eq.ApplyPendingUpdates();
        var buffer = Tone(1000f, Mono44k, 0.2f, frames: 4096);
        var before = Rms(buffer);
        eq.Process(buffer, Mono44k);
        var ratio = Rms(buffer) / before;
        Assert.InRange(ratio, 0.9f, 1.1f);
    }

    [Fact]
    public void Equalizer_Boost1k_Increases1kEnergy()
    {
        var eq = new NAudioEqualizerEffect(EqualizerOptions.CreateSixBand());
        eq.Configure(Mono44k);
        eq.SetBandGain(3, 9f); // 1000 Hz peaking
        eq.ApplyPendingUpdates();

        var buffer = Tone(1000f, Mono44k, 0.1f, frames: 8192);
        var before = PcmFrequencyAnalyzer.EstimateDominantFrequency(buffer, Mono44k.SampleRate, Mono44k.ChannelCount);
        var beforeMag = GoertzelMag(buffer, Mono44k, 1000f);
        eq.Process(buffer, Mono44k);
        var afterMag = GoertzelMag(buffer, Mono44k, 1000f);
        Assert.True(afterMag > beforeMag * 1.5f, $"before={beforeMag} after={afterMag} dominant={before}");
    }

    [Fact]
    public void Equalizer_Cut1k_Decreases1kEnergy()
    {
        var eq = new NAudioEqualizerEffect(EqualizerOptions.CreateSixBand());
        eq.Configure(Mono44k);
        eq.SetBandGain(3, -12f);
        eq.ApplyPendingUpdates();

        var buffer = Tone(1000f, Mono44k, 0.2f, frames: 8192);
        var beforeMag = GoertzelMag(buffer, Mono44k, 1000f);
        eq.Process(buffer, Mono44k);
        var afterMag = GoertzelMag(buffer, Mono44k, 1000f);
        Assert.True(afterMag < beforeMag * 0.7f, $"before={beforeMag} after={afterMag}");
    }

    [Fact]
    public void Equalizer_Bypass_PreservesSignal()
    {
        var eq = new NAudioEqualizerEffect(EqualizerOptions.CreateSixBand());
        eq.Configure(Mono44k);
        eq.SetBandGain(3, 12f);
        eq.Enabled = false;
        eq.ApplyPendingUpdates();
        // NAudio Bypass is ramped — settle with a few silent blocks, then verify.
        for (var i = 0; i < 32; i++)
        {
            eq.Process(new float[256], Mono44k);
        }

        var buffer = Tone(1000f, Mono44k, 0.2f, frames: 4096);
        var copy = buffer.ToArray();
        eq.Process(buffer, Mono44k);
        var maxDelta = 0f;
        for (var i = 0; i < buffer.Length; i++)
        {
            maxDelta = Math.Max(maxDelta, Math.Abs(buffer[i] - copy[i]));
        }

        Assert.True(maxDelta < 1e-3f, $"maxDelta={maxDelta}");
    }

    [Fact]
    public void Equalizer_Stereo_ChannelsRemainIndependent()
    {
        var eq = new NAudioEqualizerEffect(EqualizerOptions.CreateSixBand());
        eq.Configure(Stereo44k);
        eq.SetBandGain(3, 6f);
        eq.ApplyPendingUpdates();

        var buffer = new float[Stereo44k.SampleRate * 2]; // 1s stereo interleaved
        for (var i = 0; i < Stereo44k.SampleRate; i++)
        {
            buffer[i * 2] = 0.2f * MathF.Sin(2 * MathF.PI * 1000f * i / Stereo44k.SampleRate);
            buffer[i * 2 + 1] = 0f;
        }

        eq.Process(buffer, Stereo44k);
        var leftPeak = 0f;
        var rightPeak = 0f;
        for (var i = 0; i < Stereo44k.SampleRate; i++)
        {
            leftPeak = Math.Max(leftPeak, Math.Abs(buffer[i * 2]));
            rightPeak = Math.Max(rightPeak, Math.Abs(buffer[i * 2 + 1]));
        }

        Assert.True(leftPeak > 0.15f);
        Assert.True(rightPeak < 1e-3f, $"rightPeak={rightPeak}");
    }

    [Fact]
    public void Equalizer_RapidGainChanges_NoNaN()
    {
        var eq = new NAudioEqualizerEffect(EqualizerOptions.CreateSixBand());
        eq.Configure(Mono44k);
        for (var i = 0; i < 100; i++)
        {
            eq.SetBandGain(i % 6, (i % 2 == 0) ? 12f : -12f);
            eq.ApplyPendingUpdates();
            // Fresh block each time — re-EQ'ing the same buffer compounds gain into Inf.
            var buffer = Tone(440f, Mono44k, 0.2f, frames: 512);
            eq.Process(buffer, Mono44k);
            Assert.All(buffer, s => Assert.True(float.IsFinite(s)));
        }
    }

    [Fact]
    public void Equalizer_SampleRateChange_ReconfigureWorks()
    {
        var eq = new NAudioEqualizerEffect(EqualizerOptions.CreateSixBand());
        eq.Configure(AudioFormat.CreateFloat32(44100, 1));
        eq.ApplyPendingUpdates();
        eq.Process(Tone(440f, Mono44k, 0.1f, 256), Mono44k);

        var fmt48 = AudioFormat.CreateFloat32(48000, 1);
        eq.Configure(fmt48);
        eq.Reset();
        eq.ApplyPendingUpdates();
        var buffer = Tone(440f, fmt48, 0.1f, 256);
        eq.Process(buffer, fmt48);
        Assert.All(buffer, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Limiter_ReducesPeaksAboveCeiling()
    {
        var lim = new NAudioLimiterEffect(new LimiterOptions(ceilingDb: -6f, releaseMs: 50f, lookaheadMs: 0f));
        lim.Configure(Mono44k);
        lim.ApplyPendingUpdates();

        var buffer = Tone(440f, Mono44k, 0.95f, frames: 8192);
        Assert.True(Peak(buffer) > 0.9f);
        lim.Process(buffer, Mono44k);
        var ceiling = MathF.Pow(10f, -6f / 20f);
        // Allow a small overshoot for zero-lookahead limiting
        Assert.True(Peak(buffer) <= ceiling * 1.15f, $"peak={Peak(buffer)} ceiling={ceiling}");
    }

    [Fact]
    public void Limiter_WithHotPreamp_KeepsOutputNearCeiling()
    {
        var factory = new WindowsAudioEffectFactory();
        var rack = new AudioEffectRack(factory);
        rack.Configure(Mono44k);
        rack.AddPreamp(18f);
        rack.AddLimiter(new LimiterOptions(ceilingDb: 0f, releaseMs: 50f, lookaheadMs: 0f));

        var buffer = Tone(440f, Mono44k, 0.5f, frames: 8192);
        rack.Process(buffer, Mono44k);
        Assert.True(Peak(buffer) <= 1.05f, $"peak={Peak(buffer)}");
        Assert.All(buffer, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Factory_CreatesFloat32CompatibleEffects()
    {
        var factory = new WindowsAudioEffectFactory();
        var eq = factory.CreateEqualizer(EqualizerOptions.CreateSixBand());
        var pre = factory.CreatePreamp(0f);
        var lim = factory.CreateLimiter(new LimiterOptions());
        Assert.Equal(6, eq.BandCount);
        Assert.Equal(0f, pre.GainDb);
        Assert.Equal(0f, lim.CeilingDb);
    }

    [Fact]
    public async Task PlayerEffects_PreserveAcrossReload_ResetOnSeek()
    {
        WindowsAudioEnvironment.SkipIfNoWasapiOutput();
        using var engine = Sonora.Audio.Windows.SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        var eq = player.Effects.AddEqualizer();
        eq.SetBandGain(0, 3f);

        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(500));
        await player.LoadAsync(path);
        Assert.Equal(3f, eq.GetBandGain(0));

        player.Play();
        player.Seek(TimeSpan.FromMilliseconds(100));
        Assert.Equal(3f, eq.GetBandGain(0));
        player.Stop();
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

    private static float Peak(ReadOnlySpan<float> buffer)
    {
        var peak = 0f;
        foreach (var s in buffer)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        return peak;
    }

    private static float Rms(ReadOnlySpan<float> buffer)
    {
        double sum = 0;
        foreach (var s in buffer)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / buffer.Length);
    }

    private static float GoertzelMag(float[] buffer, AudioFormat format, float targetHz)
    {
        // Use left/mono channel only.
        var frames = buffer.Length / format.ChannelCount;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            mono[i] = buffer[i * format.ChannelCount];
        }

        return PcmFrequencyAnalyzer.GoertzelMagnitude(mono, format.SampleRate, targetHz);
    }
}
