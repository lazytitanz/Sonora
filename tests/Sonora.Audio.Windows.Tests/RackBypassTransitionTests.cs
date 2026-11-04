using System.Text;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Windows.DSP;
using Xunit.Abstractions;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Offline regression for click-free <see cref="AudioEffectRack.Bypass"/> transitions.
/// No WASAPI.
/// </summary>
public sealed class RackBypassTransitionTests
{
    private static readonly AudioFormat Stereo48k = AudioFormat.CreateFloat32(48_000, 2);

    private const float AnalysisWindowMs = 5f;
    private const float ClickExcessThreshold = 0.02f;
    private const float TransientEnergyThreshold = 0.002f;
    private const float NearEqualityEpsilon = 2e-3f;

    private readonly ITestOutputHelper _output;

    public RackBypassTransitionTests(ITestOutputHelper output) => _output = output;

    public enum SignalKind
    {
        Sine440,
        MultiTone,
    }

    public enum ToggleDirection
    {
        BypassOn,
        BypassOff,
    }

    private sealed record CaseSpec(
        string Name,
        Action<AudioEffectRack> Configure,
        bool ExpectClickFreeToggle);

    public static TheoryData<string, SignalKind, ToggleDirection, int> IsolationMatrix
    {
        get
        {
            var data = new TheoryData<string, SignalKind, ToggleDirection, int>();
            string[] cases =
            [
                "Empty",
                "LimiterLa5",
                "LimiterLa0",
                "FlatEq",
                "ExtremeTrebleEq",
                "PreampUnity",
                "TestPlayerDefault",
                "TestPlayerLimiterDisabled",
            ];

            foreach (var name in cases)
            {
                foreach (var signal in new[] { SignalKind.Sine440, SignalKind.MultiTone })
                {
                    foreach (var dir in new[] { ToggleDirection.BypassOn, ToggleDirection.BypassOff })
                    {
                        foreach (var blockFrames in new[] { 64, 256, 1024 })
                        {
                            data.Add(name, signal, dir, blockFrames);
                        }
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(IsolationMatrix))]
    public void RackBypass_ToggleIsClickFree(string caseName, SignalKind signal, ToggleDirection direction, int blockFrames)
    {
        var spec = CreateCase(caseName);
        var format = Stereo48k;
        var result = MeasureTransitionWorstCase(spec, signal, direction, blockFrames, format);
        WriteResult(result);

        Assert.True(float.IsFinite(result.ExcessDiscontinuity));
        Assert.True(float.IsFinite(result.ExcessWindowEnergy));
        Assert.False(result.HadNonFiniteSamples);

        if (!spec.ExpectClickFreeToggle)
        {
            return;
        }

        Assert.True(
            result.ExcessDiscontinuity < ClickExcessThreshold,
            $"{caseName}/{direction}: excessΔ={result.ExcessDiscontinuity:G6} exceeded {ClickExcessThreshold}.");
        Assert.True(
            result.ExcessWindowEnergy < TransientEnergyThreshold,
            $"{caseName}/{direction}: winEnergy={result.ExcessWindowEnergy:G6} exceeded {TransientEnergyThreshold}.");
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void TestPlayerDefault_ToggleClickFree_AcrossSampleRates(int sampleRate)
    {
        var format = AudioFormat.CreateFloat32(sampleRate, 2);
        var spec = CreateCase("TestPlayerDefault");
        foreach (var dir in new[] { ToggleDirection.BypassOn, ToggleDirection.BypassOff })
        {
            var result = MeasureTransitionWorstCase(spec, SignalKind.Sine440, dir, 256, format);
            WriteResult(result);
            Assert.True(result.ExcessDiscontinuity < ClickExcessThreshold, $"{dir} excessΔ={result.ExcessDiscontinuity:G6}");
            Assert.True(result.ExcessWindowEnergy < TransientEnergyThreshold, $"{dir} energy={result.ExcessWindowEnergy:G6}");
        }
    }

    [Fact]
    public void EmptyRack_IsBitExactTransparent()
    {
        var format = Stereo48k;
        using var rack = CreateConfiguredRack(CreateCase("Empty"), format);
        var input = RenderContinuous(SignalKind.MultiTone, 2048, format);
        var output = (float[])input.Clone();
        rack.Process(output, format);
        Assert.Equal(input, output);
    }

    [Fact]
    public void WetPath_MatchesReferenceWhenNotBypassed()
    {
        var format = Stereo48k;
        var input = RenderContinuous(SignalKind.Sine440, format.SampleRate / 5, format);

        using var a = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        using var b = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        a.Bypass = false;
        b.Bypass = false;
        a.ResetProcessingState();
        b.ResetProcessingState();

        var outA = (float[])input.Clone();
        var outB = (float[])input.Clone();
        ProcessInBlocks(a, outA, format, 256, 0, outA.Length / format.ChannelCount);
        ProcessInBlocks(b, outB, format, 256, 0, outB.Length / format.ChannelCount);

        Assert.Equal(outA.Length, outB.Length);
        AssertNearEqual(outA, outB, NearEqualityEpsilon);
    }

    [Fact]
    public void BypassedLimiterLa5_MatchesDelayedDry()
    {
        var format = Stereo48k;
        const int latencyFrames = 240; // 5 ms @ 48 kHz
        var totalFrames = format.SampleRate / 2;
        var input = RenderContinuous(SignalKind.Sine440, totalFrames, format);

        using var rack = CreateConfiguredRack(CreateCase("LimiterLa5"), format);
        rack.Bypass = true;
        rack.ResetProcessingState();

        var output = (float[])input.Clone();
        ProcessInBlocks(rack, output, format, 256, 0, totalFrames);

        // After crossfade settle (~8 ms) and delay priming, compare to manually delayed dry.
        var settleFrames = (int)(AudioEffectRack.CrossfadeMs * format.SampleRate / 1000.0) + latencyFrames + 256;
        for (var frame = settleFrames; frame < totalFrames; frame++)
        {
            for (var ch = 0; ch < format.ChannelCount; ch++)
            {
                var actual = output[frame * format.ChannelCount + ch];
                var expected = input[(frame - latencyFrames) * format.ChannelCount + ch];
                Assert.True(
                    Math.Abs(actual - expected) < NearEqualityEpsilon,
                    $"frame={frame} ch={ch} actual={actual} expected={expected}");
            }
        }
    }

    [Fact]
    public void LatencyRetarget_WhileBypassed_LimiterEnableDisable_StaysClickFree()
    {
        var format = Stereo48k;
        const int blockFrames = 256;
        var warmup = format.SampleRate / 5;

        using var rack = new AudioEffectRack(new WindowsAudioEffectFactory());
        rack.Configure(format);
        rack.AddPreamp(0f);
        rack.AddEqualizer(EqualizerOptions.CreateSixBand());
        var lim = rack.AddLimiter(new LimiterOptions(lookaheadMs: 5f));
        lim.Enabled = false;
        rack.Bypass = true;
        rack.ResetProcessingState();

        var totalFrames = warmup + blockFrames * 40;
        var dry = RenderContinuous(SignalKind.Sine440, totalFrames, format);
        var output = (float[])dry.Clone();
        ProcessInBlocks(rack, output, format, blockFrames, 0, warmup);

        // Enable limiter (latency 0 → 240) while bypassed.
        lim.Enabled = true;
        var enableAt = warmup;
        ProcessInBlocks(rack, output, format, blockFrames, enableAt, blockFrames * 20);

        // Disable again while bypassed.
        lim.Enabled = false;
        var disableAt = enableAt + blockFrames * 20;
        ProcessInBlocks(rack, output, format, blockFrames, disableAt, blockFrames * 20);

        // Dual-tap latency retarget intentionally morphs dry over ~8 ms; that raises short-window
        // energy vs unbroken dry, but must not produce a single-sample boundary click (clear+snap).
        const float retargetEnergyBudget = 0.05f;
        var e1 = ScorePathSwitch(output, dry, dry, enableAt, format, AnalysisWindowMs);
        var e2 = ScorePathSwitch(output, dry, dry, disableAt, format, AnalysisWindowMs);
        _output.WriteLine($"enable excessΔ={e1.Excess:G6} energy={e1.Energy:G6}; disable excessΔ={e2.Excess:G6} energy={e2.Energy:G6}");

        Assert.True(e1.Excess < ClickExcessThreshold, $"enable retarget excessΔ={e1.Excess:G6}");
        Assert.True(e2.Excess < ClickExcessThreshold, $"disable retarget excessΔ={e2.Excess:G6}");
        Assert.True(e1.Energy < retargetEnergyBudget, $"enable retarget energy={e1.Energy:G6}");
        Assert.True(e2.Energy < retargetEnergyBudget, $"disable retarget energy={e2.Energy:G6}");
    }

    [Fact]
    public void RapidToggles_TestPlayerDefault_StayWithinClickBudget()
    {
        var format = Stereo48k;
        const int blockFrames = 256;
        const int toggles = 16;
        var warmupFrames = format.SampleRate / 10;
        var totalFrames = warmupFrames + (toggles + 4) * blockFrames;

        using var rack = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        var dry = RenderContinuous(SignalKind.MultiTone, totalFrames, format);
        var output = (float[])dry.Clone();
        var wetRef = (float[])dry.Clone();
        var dryRef = (float[])dry.Clone();

        using var wetRack = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        using var dryRack = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        wetRack.Bypass = false;
        dryRack.Bypass = true;
        wetRack.ResetProcessingState();
        dryRack.ResetProcessingState();
        ProcessInBlocks(wetRack, wetRef, format, blockFrames, 0, totalFrames);
        ProcessInBlocks(dryRack, dryRef, format, blockFrames, 0, totalFrames);

        rack.Bypass = false;
        rack.ResetProcessingState();
        ProcessInBlocks(rack, output, format, blockFrames, 0, warmupFrames);

        var clickCount = 0;
        var bypass = false;
        for (var t = 0; t < toggles; t++)
        {
            var boundary = warmupFrames + t * blockFrames;
            bypass = !bypass;
            rack.Bypass = bypass;
            ProcessInBlocks(rack, output, format, blockFrames, boundary, blockFrames);
            var score = ScorePathSwitch(output, wetRef, dryRef, boundary, format, AnalysisWindowMs);
            if (score.Excess >= ClickExcessThreshold || score.Energy >= TransientEnergyThreshold)
            {
                clickCount++;
            }
        }

        _output.WriteLine($"Rapid toggles over budget: {clickCount}/{toggles}");
        Assert.Equal(0, clickCount);
    }

    [Fact]
    public void RetargetDuringCrossfade_DoesNotJump()
    {
        var format = Stereo48k;
        const int blockFrames = 64;
        using var rack = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        var frames = format.SampleRate / 5;
        var dry = RenderContinuous(SignalKind.Sine440, frames, format);
        var output = (float[])dry.Clone();
        rack.Bypass = false;
        rack.ResetProcessingState();
        ProcessInBlocks(rack, output, format, blockFrames, 0, blockFrames * 10);

        // Start bypass, then reverse after ~2 ms (before 8 ms completes).
        rack.Bypass = true;
        ProcessInBlocks(rack, output, format, blockFrames, blockFrames * 10, blockFrames * 2);
        rack.Bypass = false;
        var reverseAt = blockFrames * 12;
        ProcessInBlocks(rack, output, format, blockFrames, reverseAt, blockFrames * 20);

        using var wetRack = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        using var dryRack = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        var wetRef = (float[])dry.Clone();
        var dryRef = (float[])dry.Clone();
        wetRack.Bypass = false;
        dryRack.Bypass = true;
        wetRack.ResetProcessingState();
        dryRack.ResetProcessingState();
        ProcessInBlocks(wetRack, wetRef, format, blockFrames, 0, frames);
        ProcessInBlocks(dryRack, dryRef, format, blockFrames, 0, frames);

        var score = ScorePathSwitch(output, wetRef, dryRef, reverseAt, format, AnalysisWindowMs);
        _output.WriteLine($"mid-fade reverse excessΔ={score.Excess:G6} energy={score.Energy:G6}");
        Assert.True(score.Excess < ClickExcessThreshold);
        Assert.True(score.Energy < TransientEnergyThreshold);
    }

    [Fact]
    public void ResetDuringTransition_SnapsWithoutNonFinite()
    {
        var format = Stereo48k;
        using var rack = CreateConfiguredRack(CreateCase("TestPlayerDefault"), format);
        var buffer = RenderContinuous(SignalKind.Sine440, 2048, format);
        rack.Bypass = false;
        rack.Process(buffer.AsSpan(0, 512), format);
        rack.Bypass = true;
        rack.Process(buffer.AsSpan(512, 256), format);
        rack.ResetProcessingState();
        rack.Process(buffer.AsSpan(768, 256), format);
        Assert.All(buffer, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void MeasureEffectiveWetLatency_LimiterLookaheadMatrix()
    {
        var format = Stereo48k;
        var la5 = MeasureWetLatencyFrames(rack => rack.AddLimiter(new LimiterOptions(lookaheadMs: 5f)), format);
        var la0 = MeasureWetLatencyFrames(rack => rack.AddLimiter(new LimiterOptions(lookaheadMs: 0f)), format);
        var empty = MeasureWetLatencyFrames(_ => { }, format);
        var configuredLa5Frames = (int)Math.Round(5.0 * format.SampleRate / 1000.0);

        _output.WriteLine($"latency empty={empty} la0={la0} la5={la5} (cfg≈{configuredLa5Frames})");
        Assert.Equal(0, empty);
        Assert.True(la0 >= 1);
        Assert.True(Math.Abs(la5 - configuredLa5Frames) <= 2);
    }

    [Fact]
    public void Summary_ReportBothDirections()
    {
        var sb = new StringBuilder();
        sb.AppendLine("case | dir | excessΔ | energy | significant?");
        foreach (var name in new[]
                 {
                     "Empty", "LimiterLa5", "LimiterLa0", "FlatEq", "ExtremeTrebleEq",
                     "PreampUnity", "TestPlayerDefault", "TestPlayerLimiterDisabled",
                 })
        {
            var spec = CreateCase(name);
            foreach (var dir in new[] { ToggleDirection.BypassOn, ToggleDirection.BypassOff })
            {
                var r = MeasureTransitionWorstCase(spec, SignalKind.Sine440, dir, 256, Stereo48k);
                var sig = r.ExcessDiscontinuity >= ClickExcessThreshold || r.ExcessWindowEnergy >= TransientEnergyThreshold;
                sb.AppendLine($"{name} | {dir} | {r.ExcessDiscontinuity:G6} | {r.ExcessWindowEnergy:G6} | {sig}");
            }
        }

        _output.WriteLine(sb.ToString());
    }

    private static CaseSpec CreateCase(string name) => name switch
    {
        "Empty" => new CaseSpec("Empty", _ => { }, ExpectClickFreeToggle: true),
        "LimiterLa5" => new CaseSpec(
            "LimiterLa5",
            rack => rack.AddLimiter(new LimiterOptions(lookaheadMs: 5f)),
            ExpectClickFreeToggle: true),
        "LimiterLa0" => new CaseSpec(
            "LimiterLa0",
            rack => rack.AddLimiter(new LimiterOptions(lookaheadMs: 0f)),
            ExpectClickFreeToggle: true),
        "FlatEq" => new CaseSpec(
            "FlatEq",
            rack => rack.AddEqualizer(EqualizerOptions.CreateSixBand()),
            ExpectClickFreeToggle: true),
        "ExtremeTrebleEq" => new CaseSpec(
            "ExtremeTrebleEq",
            rack =>
            {
                var eq = rack.AddEqualizer(EqualizerOptions.CreateSixBand());
                eq.SetBandGain(4, 12f);
                eq.SetBandGain(5, 18f);
            },
            ExpectClickFreeToggle: true),
        "PreampUnity" => new CaseSpec(
            "PreampUnity",
            rack => rack.AddPreamp(0f),
            ExpectClickFreeToggle: true),
        "TestPlayerDefault" => new CaseSpec(
            "TestPlayerDefault",
            rack =>
            {
                rack.AddPreamp(0f);
                rack.AddEqualizer(EqualizerOptions.CreateSixBand());
                rack.AddLimiter(new LimiterOptions());
            },
            ExpectClickFreeToggle: true),
        "TestPlayerLimiterDisabled" => new CaseSpec(
            "TestPlayerLimiterDisabled",
            rack =>
            {
                rack.AddPreamp(0f);
                rack.AddEqualizer(EqualizerOptions.CreateSixBand());
                var lim = rack.AddLimiter(new LimiterOptions());
                lim.Enabled = false;
            },
            ExpectClickFreeToggle: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private sealed record TransitionResult(
        string CaseName,
        ToggleDirection Direction,
        SignalKind Signal,
        int BlockFrames,
        float ExcessDiscontinuity,
        float ExcessWindowEnergy,
        bool HadNonFiniteSamples,
        int PhasesScanned);

    private TransitionResult MeasureTransitionWorstCase(
        CaseSpec spec,
        SignalKind signal,
        ToggleDirection direction,
        int blockFrames,
        AudioFormat format)
    {
        var channels = format.ChannelCount;
        var analysisFrames = Math.Max(1, (int)Math.Round(AnalysisWindowMs * format.SampleRate / 1000.0));
        var warmupFrames = AlignUp(Math.Max(format.SampleRate / 5, blockFrames * 8), blockFrames);
        var freezeFrames = AlignUp(Math.Max(format.SampleRate / 50, blockFrames * 2), blockFrames);
        var postFrames = AlignUp(Math.Max(format.SampleRate / 10, blockFrames * 8), blockFrames);
        var phaseCount = Math.Max(8, (format.SampleRate / 40) / blockFrames);
        var scanSpan = phaseCount * blockFrames;

        int totalFrames = direction == ToggleDirection.BypassOn
            ? AlignUp(warmupFrames + scanSpan + postFrames, blockFrames)
            : AlignUp(warmupFrames + freezeFrames + scanSpan + postFrames, blockFrames);

        var drySource = RenderContinuous(signal, totalFrames, format);

        using var wetRack = CreateConfiguredRack(spec, format);
        using var dryRack = CreateConfiguredRack(spec, format);
        var wetRef = (float[])drySource.Clone();
        var dryRef = (float[])drySource.Clone();
        wetRack.Bypass = false;
        dryRack.Bypass = true;
        wetRack.ResetProcessingState();
        dryRack.ResetProcessingState();
        ProcessInBlocks(wetRack, wetRef, format, blockFrames, 0, totalFrames);
        ProcessInBlocks(dryRack, dryRef, format, blockFrames, 0, totalFrames);

        float bestExcess = 0;
        float bestEnergy = 0;
        var phases = 0;
        var hadNonFinite = ContainsNonFinite(wetRef) || ContainsNonFinite(dryRef);

        if (direction == ToggleDirection.BypassOn)
        {
            for (var phase = 0; phase < phaseCount; phase++)
            {
                var toggleFrame = warmupFrames + phase * blockFrames;
                phases++;
                using var tog = CreateConfiguredRack(spec, format);
                var toggled = (float[])drySource.Clone();
                tog.Bypass = false;
                tog.ResetProcessingState();
                ProcessInBlocks(tog, toggled, format, blockFrames, 0, toggleFrame);
                tog.Bypass = true;
                ProcessInBlocks(tog, toggled, format, blockFrames, toggleFrame, totalFrames - toggleFrame);
                if (ContainsNonFinite(toggled))
                {
                    hadNonFinite = true;
                }

                var score = ScorePathSwitch(toggled, wetRef, dryRef, toggleFrame, format, AnalysisWindowMs);
                bestExcess = Math.Max(bestExcess, score.Excess);
                bestEnergy = Math.Max(bestEnergy, score.Energy);
            }
        }
        else
        {
            for (var phase = 0; phase < phaseCount; phase++)
            {
                var freezeStart = warmupFrames;
                var toggleFrame = freezeStart + freezeFrames + phase * blockFrames;
                if (toggleFrame + analysisFrames >= totalFrames)
                {
                    break;
                }

                phases++;
                using var tog = CreateConfiguredRack(spec, format);
                var toggled = (float[])drySource.Clone();
                tog.Bypass = false;
                tog.ResetProcessingState();
                ProcessInBlocks(tog, toggled, format, blockFrames, 0, freezeStart);
                tog.Bypass = true;
                ProcessInBlocks(tog, toggled, format, blockFrames, freezeStart, toggleFrame - freezeStart);
                tog.Bypass = false;
                ProcessInBlocks(tog, toggled, format, blockFrames, toggleFrame, totalFrames - toggleFrame);
                if (ContainsNonFinite(toggled))
                {
                    hadNonFinite = true;
                }

                var score = ScorePathSwitch(toggled, wetRef, dryRef, toggleFrame, format, AnalysisWindowMs);
                bestExcess = Math.Max(bestExcess, score.Excess);
                bestEnergy = Math.Max(bestEnergy, score.Energy);
            }
        }

        return new TransitionResult(
            spec.Name, direction, signal, blockFrames, bestExcess, bestEnergy, hadNonFinite, phases);
    }

    private void WriteResult(TransitionResult r)
    {
        _output.WriteLine(
            $"{r.CaseName}/{r.Direction}/{r.Signal}/block={r.BlockFrames}: " +
            $"worstExcessΔ={r.ExcessDiscontinuity:G6}, winEnergy={r.ExcessWindowEnergy:G6}, " +
            $"phases={r.PhasesScanned}, nonFinite={r.HadNonFiniteSamples}");
    }

    private static (float Excess, float Energy) ScorePathSwitch(
        float[] toggled,
        float[] wetRef,
        float[] dryRef,
        int toggleFrame,
        AudioFormat format,
        float windowMs)
    {
        var channels = format.ChannelCount;
        var analysisFrames = Math.Max(1, (int)Math.Round(windowMs * format.SampleRate / 1000.0));
        var yPrev = toggled[(toggleFrame - 1) * channels];
        var yNext = toggled[toggleFrame * channels];
        var wetJump = Math.Abs(wetRef[toggleFrame * channels] - wetRef[(toggleFrame - 1) * channels]);
        var dryJump = Math.Abs(dryRef[toggleFrame * channels] - dryRef[(toggleFrame - 1) * channels]);
        var jump = Math.Abs(yNext - yPrev);
        var excess = Math.Max(0f, jump - Math.Max(wetJump, dryJump));

        double energy = 0;
        for (var frame = toggleFrame; frame < toggleFrame + analysisFrames; frame++)
        {
            var dy = Math.Abs(toggled[frame * channels] - toggled[(frame - 1) * channels]);
            var dRef = Math.Max(
                Math.Abs(wetRef[frame * channels] - wetRef[(frame - 1) * channels]),
                Math.Abs(dryRef[frame * channels] - dryRef[(frame - 1) * channels]));
            var e = dy - dRef;
            if (e > 0)
            {
                energy += e * e;
            }
        }

        return (excess, (float)energy);
    }

    private static AudioEffectRack CreateConfiguredRack(CaseSpec spec, AudioFormat format)
    {
        var rack = new AudioEffectRack(new WindowsAudioEffectFactory());
        rack.Configure(format);
        spec.Configure(rack);
        // Apply pending once.
        rack.Bypass = false;
        rack.ResetProcessingState();
        var scratch = new float[format.ChannelCount];
        rack.Process(scratch, format);
        return rack;
    }

    private static void ProcessInBlocks(
        AudioEffectRack rack,
        float[] interleaved,
        AudioFormat format,
        int blockFrames,
        int startFrame,
        int frameCount)
    {
        var channels = format.ChannelCount;
        var end = startFrame + frameCount;
        for (var frame = startFrame; frame < end; frame += blockFrames)
        {
            var frames = Math.Min(blockFrames, end - frame);
            rack.Process(interleaved.AsSpan(frame * channels, frames * channels), format);
        }
    }

    private static float[] RenderContinuous(SignalKind kind, int frames, AudioFormat format)
    {
        var buffer = new float[frames * format.ChannelCount];
        for (var i = 0; i < frames; i++)
        {
            float s = kind switch
            {
                SignalKind.Sine440 => 0.35f * MathF.Sin(2 * MathF.PI * 440f * i / format.SampleRate),
                SignalKind.MultiTone =>
                    0.18f * MathF.Sin(2 * MathF.PI * 220f * i / format.SampleRate)
                    + 0.18f * MathF.Sin(2 * MathF.PI * 1000f * i / format.SampleRate)
                    + 0.12f * MathF.Sin(2 * MathF.PI * 5000f * i / format.SampleRate),
                _ => 0f,
            };
            for (var ch = 0; ch < format.ChannelCount; ch++)
            {
                buffer[i * format.ChannelCount + ch] = s;
            }
        }

        return buffer;
    }

    private static int MeasureWetLatencyFrames(Action<AudioEffectRack> configure, AudioFormat format)
    {
        using var rack = new AudioEffectRack(new WindowsAudioEffectFactory());
        rack.Configure(format);
        configure(rack);
        rack.Bypass = false;
        rack.ResetProcessingState();

        var silence = new float[format.SampleRate / 10 * format.ChannelCount];
        ProcessInBlocks(rack, silence, format, 256, 0, silence.Length / format.ChannelCount);

        var impulseFrames = format.SampleRate / 5;
        var dry = new float[impulseFrames * format.ChannelCount];
        dry[0] = 1f;
        if (format.ChannelCount > 1)
        {
            dry[1] = 1f;
        }

        var wet = (float[])dry.Clone();
        ProcessInBlocks(rack, wet, format, 256, 0, impulseFrames);

        var peak = 0f;
        var peakFrame = 0;
        for (var frame = 0; frame < impulseFrames; frame++)
        {
            var v = Math.Abs(wet[frame * format.ChannelCount]);
            if (v > peak)
            {
                peak = v;
                peakFrame = frame;
            }
        }

        return peak < 1e-6f ? 0 : peakFrame;
    }

    private static void AssertNearEqual(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float eps)
    {
        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
        {
            Assert.True(Math.Abs(a[i] - b[i]) <= eps, $"i={i} a={a[i]} b={b[i]}");
        }
    }

    private static bool ContainsNonFinite(ReadOnlySpan<float> buffer)
    {
        foreach (var s in buffer)
        {
            if (!float.IsFinite(s))
            {
                return true;
            }
        }

        return false;
    }

    private static int AlignUp(int value, int multiple)
        => (value + multiple - 1) / multiple * multiple;
}
