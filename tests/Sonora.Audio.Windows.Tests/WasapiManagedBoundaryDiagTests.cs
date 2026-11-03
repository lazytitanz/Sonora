using Sonora.Audio;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.Devices;
using Sonora.Audio.Windows.DSP;
using Sonora.Audio.Windows.Output;
using Sonora.Audio.Windows.Tests.Fakes;
using Xunit.Abstractions;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Read-only WASAPI diagnostic: position-gated PCM at the managed→WASAPI boundary
/// for natural promote vs manual Load. Sine sanity + optional real fixture via env.
/// </summary>
public sealed class WasapiManagedBoundaryDiagTests
{
    public const string FixtureBEnv = "SONORA_DIAG_GAPLESS_B";
    public const string FixtureAEnv = "SONORA_DIAG_GAPLESS_A";
    public const string ListenResultEnv = "SONORA_DIAG_LISTEN_RESULT";
    public const string CaptureDirEnv = "SONORA_DIAG_CAPTURE_DIR";
    public const string SkipMsEnv = "SONORA_DIAG_CAPTURE_SKIP_MS";
    public const string WindowMsEnv = "SONORA_DIAG_CAPTURE_WINDOW_MS";

    private static readonly TimeSpan DefaultSkip = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(500);

    private readonly ITestOutputHelper _output;

    public WasapiManagedBoundaryDiagTests(ITestOutputHelper output) => _output = output;

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task MidTrackPcm_SineWav_Promote_Vs_Load_PositionGated()
    {
        var aPath = EnsureNamedToneWav("wasapi-diag-a-200ms.wav", TimeSpan.FromMilliseconds(200), 220f);
        var bPath = EnsureNamedToneWav("wasapi-diag-b-8s.wav", TimeSpan.FromSeconds(8), 440f);
        await RunCompareAsync(aPath, bPath, listenResultOverride: "inconclusive");
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task MidTrackPcm_RealFixture_Promote_Vs_Load_PositionGated()
    {
        var bPath = Environment.GetEnvironmentVariable(FixtureBEnv);
        if (string.IsNullOrWhiteSpace(bPath) || !File.Exists(bPath))
        {
            // Soft-pass when hardware is enabled but no local fixture is configured.
            _output.WriteLine(
                $"SKIPPED real-fixture diag: set {FixtureBEnv} to an existing local WAV "
                + "(e.g. test-audio-wav-16bit-44khz.wav). Do not commit the file.");
            return;
        }

        var aPath = Environment.GetEnvironmentVariable(FixtureAEnv);
        if (string.IsNullOrWhiteSpace(aPath) || !File.Exists(aPath))
        {
            aPath = EnsureNamedToneWav("wasapi-diag-real-a-200ms.wav", TimeSpan.FromMilliseconds(200), 220f);
        }

        await RunCompareAsync(bPathA: aPath, bPathB: bPath!, listenResultOverride: null);
    }

    private async Task RunCompareAsync(string bPathA, string bPathB, string? listenResultOverride)
    {
        var skip = ReadTimeMs(SkipMsEnv, DefaultSkip);
        var window = ReadTimeMs(WindowMsEnv, DefaultWindow);

        var promoted = await CapturePromoteAsync(bPathA, bPathB, skip, window);
        var manual = await CaptureManualAsync(bPathB, skip, window);

        Assert.Equal(promoted.Format.SampleRate, manual.Format.SampleRate);
        Assert.Equal(promoted.Format.ChannelCount, manual.Format.ChannelCount);

        var report = RealAudioCompare.Compare(
            promoted.Samples,
            manual.Samples,
            promoted.Format.SampleRate,
            promoted.Format.ChannelCount,
            maxLagMs: 100);

        var listen = listenResultOverride
            ?? Environment.GetEnvironmentVariable(ListenResultEnv)
            ?? "inconclusive";

        _output.WriteLine($"listenResult={listen}");
        _output.WriteLine($"promote: {promoted.Notes}");
        _output.WriteLine($"manual: {manual.Notes}");
        _output.WriteLine($"compare: {report}");

        MaybeDump(promoted, manual, report, listen);

        if (!report.Suitable)
        {
            Assert.Fail($"Fixture/capture unsuitable: {report.Reason}");
        }

        if (report.AlignmentInconclusive)
        {
            Assert.Fail($"Alignment inconclusive (peakNcc={report.PeakNcc:F3}). {report}");
        }

        // PCM match without audible promote distortion must not be treated as post-boundary proof.
        if (string.Equals(listen, "promote_distorted_load_clean", StringComparison.OrdinalIgnoreCase)
            && report.AlignedMatch)
        {
            _output.WriteLine(
                "NOTE: Managed-boundary PCM matches while listen claims promote distortion — "
                + "evidence points after the managed boundary (or harness vs TestPlayer delta).");
        }

        Assert.True(
            report.AlignedMatch,
            $"Position-gated WASAPI managed-boundary promote vs Load diverged. {report} "
            + $"| promote=({promoted.Notes}) manual=({manual.Notes}) listen={listen}");
    }

    private async Task<CaptureResult> CapturePromoteAsync(
        string aPath,
        string bPath,
        TimeSpan skip,
        TimeSpan window)
    {
        var outputs = new TrackingWindowsOutputFactory();
        using var engine = CreateEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = 0.05f;

        var transitioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.TrackTransitioned += (_, _) => transitioned.TrySetResult();

        await player.LoadAsync(aPath);
        await player.PreloadNextAsync(bPath);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();
        await transitioned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(bPath, player.SourcePath);

        var captured = await ManagedOutputPositionCapture.CaptureWindowAtPositionAsync(
            player,
            () => outputs.LastWasapi,
            skip,
            window,
            timeout: TimeSpan.FromSeconds(skip.TotalSeconds + window.TotalSeconds + 30));

        player.Stop();
        return new CaptureResult(
            captured.Format,
            captured.Samples,
            $"path={Path.GetFileName(captured.SourcePath)} armedAt={captured.ArmedAtPosition.TotalMilliseconds:F0}ms "
            + $"transitionId={captured.TransitionId} silence={captured.LastSilenceReason} "
            + $"underruns={captured.UnderrunCount} mode={captured.OutputMode} n={captured.Samples.Length}");
    }

    private async Task<CaptureResult> CaptureManualAsync(string bPath, TimeSpan skip, TimeSpan window)
    {
        var outputs = new TrackingWindowsOutputFactory();
        using var engine = CreateEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = 0.05f;

        await player.LoadAsync(bPath);
        player.Play();

        var captured = await ManagedOutputPositionCapture.CaptureWindowAtPositionAsync(
            player,
            () => outputs.LastWasapi,
            skip,
            window,
            timeout: TimeSpan.FromSeconds(skip.TotalSeconds + window.TotalSeconds + 30));

        player.Stop();
        return new CaptureResult(
            captured.Format,
            captured.Samples,
            $"path={Path.GetFileName(captured.SourcePath)} armedAt={captured.ArmedAtPosition.TotalMilliseconds:F0}ms "
            + $"silence={captured.LastSilenceReason} underruns={captured.UnderrunCount} "
            + $"mode={captured.OutputMode} n={captured.Samples.Length}");
    }

    private void MaybeDump(CaptureResult promote, CaptureResult manual, RealAudioCompare.Report report, string listen)
    {
        var dir = Environment.GetEnvironmentVariable(CaptureDirEnv);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var sub = Path.Combine(dir, $"wasapi-diag-{stamp}");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "promote.f32"), FloatBytes(promote.Samples));
        File.WriteAllBytes(Path.Combine(sub, "manual.f32"), FloatBytes(manual.Samples));
        File.WriteAllText(
            Path.Combine(sub, "report.txt"),
            $"listen={listen}\npromote={promote.Notes}\nmanual={manual.Notes}\n{report}\n");
        _output.WriteLine($"Dumped captures to {sub}");
    }

    private static byte[] FloatBytes(float[] samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(float))];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static AudioEngine CreateEngine(TrackingWindowsOutputFactory outputs) =>
        new(
            new WindowsAudioDecoderFactory(),
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.FromMilliseconds(50),
                BufferDuration = TimeSpan.FromMilliseconds(500),
                EffectFactory = new WindowsAudioEffectFactory(),
                FormatConverter = new WindowsAudioFormatConverter(),
            },
            new WindowsAudioOutputDevices());

    private static string EnsureNamedToneWav(string fileName, TimeSpan duration, float frequencyHz)
    {
        var path = Path.Combine(FixtureHelper.FixturesDirectory, fileName);
        lock (typeof(FixtureHelper))
        {
            if (!File.Exists(path))
            {
                FixtureHelper.WriteToneWav(path, duration, sampleRate: 44100, channels: 2, frequencyHz);
            }
        }

        return path;
    }

    private static TimeSpan ReadTimeMs(string envName, TimeSpan fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw) || !double.TryParse(raw, out var ms) || ms < 0)
        {
            return fallback;
        }

        return TimeSpan.FromMilliseconds(ms);
    }

    private sealed record CaptureResult(AudioFormat Format, float[] Samples, string Notes);
}

/// <summary>NCC alignment + metrics for real (non-sine) managed-boundary captures.</summary>
internal static class RealAudioCompare
{
    public sealed class Report
    {
        public bool Suitable { get; init; }
        public string? Reason { get; init; }
        public bool AlignmentInconclusive { get; init; }
        public bool AlignedMatch { get; init; }
        public double PeakNcc { get; init; }
        public int BestLagSamples { get; init; }
        public double BestLagMs { get; init; }
        public float MaxAbsDelta { get; init; }
        public float RmsDelta { get; init; }
        public float PromoteRms { get; init; }
        public float ManualRms { get; init; }
        public float PromotePeak { get; init; }
        public float ManualPeak { get; init; }
        public int FirstDivergeIndex { get; init; }
        public int ComparedSamples { get; init; }
        public int ChannelCount { get; init; }

        public override string ToString() =>
            $"suitable={Suitable} match={AlignedMatch} inconclusive={AlignmentInconclusive} "
            + $"ncc={PeakNcc:F4} lag={BestLagSamples} ({BestLagMs:F2}ms) maxΔ={MaxAbsDelta:E3} "
            + $"rmsΔ={RmsDelta:E3} pRms={PromoteRms:F6} mRms={ManualRms:F6} "
            + $"pPeak={PromotePeak:F4} mPeak={ManualPeak:F4} firstDiv={FirstDivergeIndex} n={ComparedSamples}"
            + (Reason is null ? "" : $" reason={Reason}");
    }

    public static Report Compare(float[] promote, float[] manual, int sampleRate, int channels, int maxLagMs)
    {
        channels = Math.Max(1, channels);
        var promoteRms = Rms(promote);
        var manualRms = Rms(manual);
        if (promote.Length < sampleRate * channels / 10 || manual.Length < sampleRate * channels / 10)
        {
            return new Report { Suitable = false, Reason = "Capture too short." };
        }

        if (promoteRms < 1e-4f || manualRms < 1e-4f)
        {
            return new Report { Suitable = false, Reason = "Capture RMS below silence floor." };
        }

        var maxLag = (int)(sampleRate * (maxLagMs / 1000.0) * channels);
        maxLag -= maxLag % channels;
        maxLag = Math.Max(channels, maxLag);

        var bestLag = 0;
        var bestNcc = double.NegativeInfinity;
        for (var lag = -maxLag; lag <= maxLag; lag += channels)
        {
            if (!TryOverlap(promote, manual, lag, out var p, out var m))
            {
                continue;
            }

            var ncc = Ncc(p, m);
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestLag = lag;
            }
        }

        if (!TryOverlap(promote, manual, bestLag, out var bp, out var bm))
        {
            return new Report { Suitable = false, Reason = "No overlap at best lag." };
        }

        const double minNcc = 0.85;
        var (max, rmsD, pRms, mRms, first) = Deltas(bp, bm);
        return new Report
        {
            Suitable = true,
            AlignmentInconclusive = bestNcc < minNcc,
            AlignedMatch = bestNcc >= minNcc && max < 1e-4f && first < 0,
            PeakNcc = bestNcc,
            BestLagSamples = bestLag,
            BestLagMs = bestLag / (double)(sampleRate * channels) * 1000.0,
            MaxAbsDelta = max,
            RmsDelta = rmsD,
            PromoteRms = pRms,
            ManualRms = mRms,
            PromotePeak = Peak(bp),
            ManualPeak = Peak(bm),
            FirstDivergeIndex = first,
            ComparedSamples = Math.Min(bp.Length, bm.Length),
            ChannelCount = channels,
        };
    }

    private static double Ncc(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        double meanA = 0, meanB = 0;
        for (var i = 0; i < n; i++)
        {
            meanA += a[i];
            meanB += b[i];
        }

        meanA /= n;
        meanB /= n;
        double num = 0, denA = 0, denB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            num += da * db;
            denA += da * da;
            denB += db * db;
        }

        var den = Math.Sqrt(denA * denB);
        return den < 1e-20 ? 0 : num / den;
    }

    private static bool TryOverlap(
        ReadOnlySpan<float> promote,
        ReadOnlySpan<float> manual,
        int lag,
        out ReadOnlySpan<float> p,
        out ReadOnlySpan<float> m)
    {
        if (lag >= 0)
        {
            if (lag >= promote.Length || promote.Length - lag < 64 || manual.Length < 64)
            {
                p = default;
                m = default;
                return false;
            }

            var n = Math.Min(promote.Length - lag, manual.Length);
            p = promote.Slice(lag, n);
            m = manual[..n];
            return n >= 64;
        }

        var skipM = -lag;
        if (skipM >= manual.Length || manual.Length - skipM < 64 || promote.Length < 64)
        {
            p = default;
            m = default;
            return false;
        }

        var n2 = Math.Min(promote.Length, manual.Length - skipM);
        p = promote[..n2];
        m = manual.Slice(skipM, n2);
        return n2 >= 64;
    }

    private static (float Max, float RmsD, float PRms, float MRms, int First) Deltas(
        ReadOnlySpan<float> promote,
        ReadOnlySpan<float> manual)
    {
        var n = Math.Min(promote.Length, manual.Length);
        double sumSq = 0, pSq = 0, mSq = 0;
        float max = 0;
        var first = -1;
        for (var i = 0; i < n; i++)
        {
            var d = Math.Abs(promote[i] - manual[i]);
            if (d > max)
            {
                max = d;
            }

            if (first < 0 && d > 1e-4f)
            {
                first = i;
            }

            sumSq += d * d;
            pSq += promote[i] * promote[i];
            mSq += manual[i] * manual[i];
        }

        return (
            max,
            n > 0 ? (float)Math.Sqrt(sumSq / n) : 0,
            n > 0 ? (float)Math.Sqrt(pSq / n) : 0,
            n > 0 ? (float)Math.Sqrt(mSq / n) : 0,
            first);
    }

    private static float Rms(ReadOnlySpan<float> s)
    {
        if (s.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < s.Length; i++)
        {
            sum += s[i] * s[i];
        }

        return (float)Math.Sqrt(sum / s.Length);
    }

    private static float Peak(ReadOnlySpan<float> s)
    {
        float peak = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var a = Math.Abs(s[i]);
            if (a > peak)
            {
                peak = a;
            }
        }

        return peak;
    }
}
