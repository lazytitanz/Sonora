using Sonora.Audio.Abstractions;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Phase 1 gapless crackle investigation: DSP bypass verification and mid-track PCM
/// compare (natural promote of B vs manual Load of B). Not a product regression gate —
/// reports quantitative evidence via assertion messages when mismatched.
/// </summary>
public sealed class GaplessPromotePcmPhase1Tests
{
    private static readonly AudioFormat Mono = AudioFormat.CreateFloat32(44100, 1);
    private static readonly AudioFormat Stereo = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public void RackBypass_SkipsEffectProcess_VolumeStillApplies()
    {
        var factory = new FakeAudioEffectFactory();
        var rack = new AudioEffectRack(factory);
        var pre = (FakePreampEffect)rack.AddPreamp(gainDb: 6f);
        rack.Configure(Mono);

        var buffer = new float[64];
        Array.Fill(buffer, 0.2f);
        rack.Bypass = true;
        rack.Process(buffer, Mono);

        Assert.Equal(0, pre.ProcessCount);
        Assert.All(buffer, s => Assert.Equal(0.2f, s));

        rack.Bypass = false;
        rack.Process(buffer, Mono);
        Assert.Equal(1, pre.ProcessCount);
        // +6 dB ≈ ×2
        Assert.All(buffer, s => Assert.True(s > 0.35f && s < 0.45f));
    }

    [Fact]
    public async Task MidTrackPcm_NaturalPromote_Vs_ManualLoad_MonoMarker()
    {
        // A: short marker 0.1; B: long marker 0.7 — mid-track window of B should match Load path.
        var aSamples = CreateConstant(Mono, TimeSpan.FromMilliseconds(120), 0.1f);
        var bSamples = CreateConstant(Mono, TimeSpan.FromSeconds(2), 0.7f);
        var report = await CompareMidTrackAsync(Mono, aSamples, bSamples, bypassRack: true);
        Assert.True(
            report.MaxAbsDelta < 1e-4f,
            report.ToString());
    }

    [Fact]
    public async Task MidTrackPcm_NaturalPromote_Vs_ManualLoad_StereoLrMarkers()
    {
        // Stereo L=+0.6 R=-0.6 — detects channel swap / frame misalignment lasting into B.
        var aSamples = CreateStereoMarkers(TimeSpan.FromMilliseconds(120), 0.05f, -0.05f);
        var bSamples = CreateStereoMarkers(TimeSpan.FromSeconds(2), 0.6f, -0.6f);
        var report = await CompareMidTrackAsync(Stereo, aSamples, bSamples, bypassRack: true);
        Assert.True(
            report.MaxAbsDelta < 1e-4f && report.StereoCorrelationSign < 0,
            report.ToString());
    }

    [Fact]
    public async Task MidTrackPcm_NaturalPromote_WithRackEnabled_StillMatchesManualLoad()
    {
        // Even with identity fake EQ, promote vs load should match if PCM path is identical.
        var aSamples = CreateConstant(Mono, TimeSpan.FromMilliseconds(100), 0.15f);
        var bSamples = CreateConstant(Mono, TimeSpan.FromSeconds(2), 0.55f);
        var report = await CompareMidTrackAsync(Mono, aSamples, bSamples, bypassRack: false);
        Assert.True(
            report.MaxAbsDelta < 1e-4f,
            report.ToString());
    }

    [Fact]
    public async Task Diagnostics_AfterNaturalPromote_NoPendingNextOrUnderrunStorm()
    {
        var aSamples = CreateConstant(Mono, TimeSpan.FromMilliseconds(80), 0.2f);
        var bSamples = CreateConstant(Mono, TimeSpan.FromSeconds(1.5), 0.4f);
        FakeAudioOutput? output = null;
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            var samples = path == "a.wav" ? aSamples : bSamples;
            return Task.FromResult<IAudioDecoder>(new FakeAudioDecoder(Mono, (float[])samples.Clone()));
        });
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 128, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero, BufferDuration = TimeSpan.FromMilliseconds(200) });
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;

        var transitioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaybackDiagnostics? atTransition = null;
        player.TrackTransitioned += (_, _) =>
        {
            atTransition = player.GetDiagnostics();
            transitioned.TrySetResult();
        };

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();

        await transitioned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        var later = player.GetDiagnostics();

        Assert.NotNull(atTransition);
        Assert.Equal("b.wav", player.SourcePath);
        Assert.True(
            atTransition!.LastSilenceReason is SeekSilenceReason.None or SeekSilenceReason.Underrun,
            $"At transition silence={atTransition.LastSilenceReason} underruns={atTransition.UnderrunCount}");
        Assert.NotEqual(SeekSilenceReason.PendingNext, later.LastSilenceReason);
        // Allow a small underrun edge at the join; storm would keep climbing.
        Assert.True(later.UnderrunCount - atTransition.UnderrunCount < 50,
            $"Underrun storm after promote: at={atTransition.UnderrunCount} later={later.UnderrunCount}");
    }

    private static async Task<PcmCompareReport> CompareMidTrackAsync(
        AudioFormat format,
        float[] aSamples,
        float[] bSamples,
        bool bypassRack)
    {
        var skipIntoB = (int)format.TimeToSamples(TimeSpan.FromMilliseconds(400));
        var window = (int)format.TimeToSamples(TimeSpan.FromMilliseconds(200));

        var promoted = await CaptureNaturalPromoteBAsync(format, aSamples, bSamples, bypassRack, skipIntoB + window);
        var manual = await CaptureManualLoadBAsync(format, bSamples, bypassRack, skipIntoB + window);

        Assert.True(
            promoted.Length >= skipIntoB + window,
            $"promote B capture short: {promoted.Length} need>={skipIntoB + window}");
        Assert.True(
            manual.Length >= skipIntoB + window,
            $"manual B capture short: {manual.Length} need>={skipIntoB + window}");

        var pWin = promoted.AsSpan(skipIntoB, window);
        var mWin = manual.AsSpan(skipIntoB, window);
        return PcmCompareReport.Compare(pWin, mWin, format.ChannelCount);
    }

    private static async Task<float[]> CaptureNaturalPromoteBAsync(
        AudioFormat format,
        float[] aSamples,
        float[] bSamples,
        bool bypassRack,
        int minBSamples)
    {
        FakeAudioOutput? output = null;
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            var samples = path == "a.wav" ? aSamples : bSamples;
            // SequenceFakeDecoder matches the proven Gapless_SpanningBuffer harness.
            return Task.FromResult<IAudioDecoder>(new SequenceFakeDecoder(format, (float[])samples.Clone()));
        });
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 128, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                BufferDuration = TimeSpan.FromMilliseconds(500),
                EffectFactory = bypassRack ? null : new FakeAudioEffectFactory(),
            });
        using var player = engine.CreatePlayer();
        if (!bypassRack)
        {
            player.Effects.AddEqualizer();
            player.Effects.Bypass = false;
        }
        else
        {
            player.Effects.Bypass = true;
        }

        var transitionIndex = -1;
        var transitioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.TrackTransitioned += (_, _) =>
        {
            if (output is not null)
            {
                lock (output.CapturedSamples)
                {
                    transitionIndex = output.CapturedSamples.Count;
                }
            }

            transitioned.TrySetResult();
        };

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();
        await transitioned.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(transitionIndex >= 0, "transition index not recorded");
        var needTotal = transitionIndex + minBSamples;
        await WaitForCaptureAsync(output!, needTotal, TimeSpan.FromSeconds(8));

        var diag = player.GetDiagnostics();
        player.Stop();

        Assert.NotNull(output);
        float[] all;
        lock (output!.CapturedSamples)
        {
            all = output.CapturedSamples.ToArray();
        }

        Assert.True(
            all.Length >= needTotal,
            $"promote total capture short: len={all.Length} need>={needTotal} transitionAt={transitionIndex} "
            + $"state={player.State} path={player.SourcePath} silence={diag.LastSilenceReason} "
            + $"underruns={diag.UnderrunCount} init={output.InitializeCount} reads={output.ReadCount} "
            + $"play={output.PlayCount} stop={output.StopCount}");

        // Slice B from the recorded transition boundary (frame-align).
        var startB = transitionIndex - (transitionIndex % Math.Max(1, format.ChannelCount));
        return all.AsSpan(startB).ToArray();
    }

    private static async Task<float[]> CaptureManualLoadBAsync(
        AudioFormat format,
        float[] bSamples,
        bool bypassRack,
        int minSamples)
    {
        FakeAudioOutput? output = null;
        var factory = new FakeAudioDecoderFactory((_, _) =>
            Task.FromResult<IAudioDecoder>(new SequenceFakeDecoder(format, (float[])bSamples.Clone())));
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 128, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                BufferDuration = TimeSpan.FromMilliseconds(500),
                EffectFactory = bypassRack ? null : new FakeAudioEffectFactory(),
            });
        using var player = engine.CreatePlayer();
        if (!bypassRack)
        {
            player.Effects.AddEqualizer();
        }

        player.Effects.Bypass = bypassRack;
        await player.LoadAsync("b.wav");
        player.Play();
        await WaitForCaptureAsync(output!, minSamples, TimeSpan.FromSeconds(8));
        player.Stop();

        Assert.NotNull(output);
        lock (output!.CapturedSamples)
        {
            return output.CapturedSamples.ToArray();
        }
    }

    private static async Task WaitForCaptureAsync(FakeAudioOutput output, int minSamples, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            int count;
            lock (output.CapturedSamples)
            {
                count = output.CapturedSamples.Count;
            }

            if (count >= minSamples)
            {
                return;
            }

            // Pull loop stopped early (EOS / failure) — no point spinning the full timeout.
            if (output.IsPullLoopIdle && count < minSamples && sw.Elapsed > TimeSpan.FromMilliseconds(200))
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static float[] CreateConstant(AudioFormat format, TimeSpan duration, float value)
    {
        var n = (int)format.TimeToSamples(duration);
        var data = new float[n];
        Array.Fill(data, value);
        return data;
    }

    private static float[] CreateStereoMarkers(TimeSpan duration, float left, float right)
    {
        var n = (int)Stereo.TimeToSamples(duration);
        var data = new float[n];
        for (var i = 0; i < n; i += 2)
        {
            data[i] = left;
            data[i + 1] = right;
        }

        return data;
    }

    private sealed class PcmCompareReport
    {
        public float MaxAbsDelta { get; init; }
        public float RmsDelta { get; init; }
        public float StereoCorrelationSign { get; init; }
        public int WindowSamples { get; init; }
        public float PromoteRms { get; init; }
        public float ManualRms { get; init; }

        public static PcmCompareReport Compare(ReadOnlySpan<float> promote, ReadOnlySpan<float> manual, int channels)
        {
            var n = Math.Min(promote.Length, manual.Length);
            double sumSq = 0;
            float max = 0;
            double pSq = 0;
            double mSq = 0;
            double corr = 0;
            for (var i = 0; i < n; i++)
            {
                var d = Math.Abs(promote[i] - manual[i]);
                if (d > max)
                {
                    max = d;
                }

                sumSq += d * d;
                pSq += promote[i] * promote[i];
                mSq += manual[i] * manual[i];
            }

            if (channels >= 2)
            {
                // Markers use opposite L/R signs → mean L*R should be negative.
                double lr = 0;
                var frames = n / channels;
                for (var f = 0; f < frames; f++)
                {
                    var i = f * channels;
                    lr += promote[i] * promote[i + 1];
                }

                corr = frames > 0 ? lr / frames : 0;
            }

            return new PcmCompareReport
            {
                MaxAbsDelta = max,
                RmsDelta = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0,
                StereoCorrelationSign = (float)corr,
                WindowSamples = n,
                PromoteRms = n > 0 ? (float)Math.Sqrt(pSq / n) : 0,
                ManualRms = n > 0 ? (float)Math.Sqrt(mSq / n) : 0,
            };
        }

        public override string ToString() =>
            $"maxΔ={MaxAbsDelta:E3} rmsΔ={RmsDelta:E3} promoteRms={PromoteRms:F4} manualRms={ManualRms:F4} " +
            $"stereoLR={StereoCorrelationSign:F4} n={WindowSamples}";
    }
}
