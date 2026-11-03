using Sonora.Audio;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.Tests.Fakes;
using Xunit.Abstractions;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Diagnostic-only: mid-track PCM after natural gapless promote of B vs manual Load of the
/// same B file, using real Windows decoders and capturing pull output (no WASAPI).
/// Isolates decoder/preload/promote pipeline from the Windows output device path.
/// </summary>
public sealed class GaplessPromotePcmWindowsDiagTests
{
    private static readonly TimeSpan SkipIntoB = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private readonly ITestOutputHelper _output;

    public GaplessPromotePcmWindowsDiagTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MidTrackPcm_SameWav_NaturalPromote_Vs_ManualLoad()
    {
        // Short A + long B (same B file reused for Load). WAV removes most codec variables.
        var aPath = EnsureNamedToneWav("diag-gapless-a-200ms.wav", TimeSpan.FromMilliseconds(200), frequencyHz: 220f);
        var bPath = EnsureNamedToneWav("diag-gapless-b-8s.wav", TimeSpan.FromSeconds(8), frequencyHz: 440f);

        var promoted = await CaptureNaturalPromoteBAsync(aPath, bPath);
        var manual = await CaptureManualLoadBAsync(bPath);

        var format = promoted.Format;
        Assert.Equal(format.SampleRate, manual.Format.SampleRate);
        Assert.Equal(format.ChannelCount, manual.Format.ChannelCount);

        var skip = (int)format.TimeToSamples(SkipIntoB);
        var window = (int)format.TimeToSamples(Window);

        Assert.True(
            promoted.Samples.Length >= skip + window,
            $"promote B short: {promoted.Samples.Length} need>={skip + window} ({promoted.Diagnostics})");
        Assert.True(
            manual.Samples.Length >= skip + window,
            $"manual B short: {manual.Samples.Length} need>={skip + window} ({manual.Diagnostics})");

        var raw = PcmDivergenceReport.Compare(
            promoted.Samples.AsSpan(skip, window),
            manual.Samples.AsSpan(skip, window),
            format.ChannelCount);

        // Pure tones at exact second boundaries hit zero-crossings; raw index-0 compare is misleading.
        // Align within ±50 ms then re-compare — phase/timeline skew vs true sample corruption.
        var maxLag = (int)format.TimeToSamples(TimeSpan.FromMilliseconds(50));
        var aligned = PcmDivergenceReport.CompareAligned(
            promoted.Samples.AsSpan(skip, window),
            manual.Samples.AsSpan(skip, window),
            format.ChannelCount,
            maxLagSamples: maxLag);

        _output.WriteLine($"raw: {raw}");
        _output.WriteLine($"aligned: {aligned}");
        _output.WriteLine($"promote: {promoted.Diagnostics}");
        _output.WriteLine($"manual: {manual.Diagnostics}");

        // Diagnostic gate after best lag:
        // - Match ⇒ decode/promote PCM matches Load (look at WASAPI/output for audible static).
        // - Still diverge ⇒ first bad samples are before the device; report lag + first index.
        Assert.True(
            aligned.MaxAbsDelta < 1e-4f && aligned.FirstDivergeIndex < 0,
            $"WAV mid-track promote vs Load diverged after alignment. raw=({raw}) aligned=({aligned}) "
            + $"| promote=({promoted.Diagnostics}) manual=({manual.Diagnostics})");
    }

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

    private static async Task<CaptureResult> CaptureNaturalPromoteBAsync(string aPath, string bPath)
    {
        PullingFakeOutput? output = null;
        var outputs = new PullingFakeOutputFactory(() =>
        {
            output = new PullingFakeOutput(
                blockSamples: 256,
                latency: TimeSpan.FromMilliseconds(1),
                captureSamples: true);
            return output;
        });

        using var engine = CreateDiagEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;

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

        await player.LoadAsync(aPath);
        await player.PreloadNextAsync(bPath);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();
        await transitioned.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(transitionIndex >= 0, "TrackTransitioned did not record a capture index");
        Assert.Equal(bPath, player.SourcePath);

        var format = output!.Format
            ?? throw new InvalidOperationException("Output format missing after promote.");
        var needFromB = (int)format.TimeToSamples(SkipIntoB + Window);
        await WaitForCaptureAsync(output, transitionIndex + needFromB, TimeSpan.FromSeconds(30));

        var diag = player.GetDiagnostics();
        player.Stop();

        float[] all;
        lock (output.CapturedSamples)
        {
            all = output.CapturedSamples.ToArray();
        }

        var startB = transitionIndex - (transitionIndex % Math.Max(1, format.ChannelCount));
        Assert.True(
            all.Length >= startB + needFromB,
            $"promote total short: len={all.Length} startB={startB} needFromB={needFromB} "
            + $"state={player.State} silence={diag.LastSilenceReason} underruns={diag.UnderrunCount} "
            + $"reads={output.ReadCount} idle={output.IsPullLoopIdle}");

        return new CaptureResult(
            format,
            all.AsSpan(startB).ToArray(),
            $"path={player.SourcePath} silence={diag.LastSilenceReason} underruns={diag.UnderrunCount} "
            + $"transitionAt={transitionIndex} total={all.Length}");
    }

    private static async Task<CaptureResult> CaptureManualLoadBAsync(string bPath)
    {
        PullingFakeOutput? output = null;
        var outputs = new PullingFakeOutputFactory(() =>
        {
            output = new PullingFakeOutput(
                blockSamples: 256,
                latency: TimeSpan.FromMilliseconds(1),
                captureSamples: true);
            return output;
        });

        using var engine = CreateDiagEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;

        await player.LoadAsync(bPath);
        player.Play();

        var format = await WaitForFormatAsync(output, TimeSpan.FromSeconds(5));
        var need = (int)format.TimeToSamples(SkipIntoB + Window);
        await WaitForCaptureAsync(output!, need, TimeSpan.FromSeconds(30));

        var diag = player.GetDiagnostics();
        player.Stop();

        float[] samples;
        lock (output!.CapturedSamples)
        {
            samples = output.CapturedSamples.ToArray();
        }

        Assert.True(
            samples.Length >= need,
            $"manual short: len={samples.Length} need>={need} silence={diag.LastSilenceReason} "
            + $"underruns={diag.UnderrunCount} idle={output.IsPullLoopIdle}");

        return new CaptureResult(
            format,
            samples,
            $"path={player.SourcePath} silence={diag.LastSilenceReason} underruns={diag.UnderrunCount} total={samples.Length}");
    }

    private static AudioEngine CreateDiagEngine(IAudioOutputFactory outputs) =>
        new(
            new WindowsAudioDecoderFactory(),
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.FromMilliseconds(50),
                BufferDuration = TimeSpan.FromMilliseconds(500),
                FormatConverter = new WindowsAudioFormatConverter(),
            });

    private static async Task<AudioFormat> WaitForFormatAsync(PullingFakeOutput? output, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (output?.Format is { } format)
            {
                return format;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Output format was not set.");
    }

    private static async Task WaitForCaptureAsync(PullingFakeOutput output, int minSamples, TimeSpan timeout)
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

            if (output.IsPullLoopIdle && count < minSamples && sw.Elapsed > TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private sealed record CaptureResult(AudioFormat Format, float[] Samples, string Diagnostics);

    private sealed class PcmDivergenceReport
    {
        public float MaxAbsDelta { get; init; }
        public float RmsDelta { get; init; }
        public float PromoteRms { get; init; }
        public float ManualRms { get; init; }
        public int FirstDivergeIndex { get; init; }
        public float FirstDivergePromote { get; init; }
        public float FirstDivergeManual { get; init; }
        public int WindowSamples { get; init; }
        public int ChannelCount { get; init; }
        public int BestLagSamples { get; init; }

        public static PcmDivergenceReport Compare(
            ReadOnlySpan<float> promote,
            ReadOnlySpan<float> manual,
            int channels,
            float epsilon = 1e-4f)
        {
            var n = Math.Min(promote.Length, manual.Length);
            return CompareFixed(promote[..n], manual[..n], channels, lag: 0, epsilon);
        }

        /// <summary>
        /// Finds lag of <paramref name="promote"/> relative to <paramref name="manual"/> that
        /// minimizes RMS delta (frame-aligned), then compares the overlapping region.
        /// Positive lag means promote is shifted later (drop promote head / manual tail).
        /// </summary>
        public static PcmDivergenceReport CompareAligned(
            ReadOnlySpan<float> promote,
            ReadOnlySpan<float> manual,
            int channels,
            int maxLagSamples,
            float epsilon = 1e-4f)
        {
            channels = Math.Max(1, channels);
            maxLagSamples -= maxLagSamples % channels;
            var bestLag = 0;
            var bestRms = double.MaxValue;

            for (var lag = -maxLagSamples; lag <= maxLagSamples; lag += channels)
            {
                if (!TryOverlap(promote, manual, lag, out var p, out var m))
                {
                    continue;
                }

                double sumSq = 0;
                for (var i = 0; i < p.Length; i++)
                {
                    var d = p[i] - m[i];
                    sumSq += d * d;
                }

                var rms = Math.Sqrt(sumSq / p.Length);
                if (rms < bestRms)
                {
                    bestRms = rms;
                    bestLag = lag;
                }
            }

            TryOverlap(promote, manual, bestLag, out var bestP, out var bestM);
            var report = CompareFixed(bestP, bestM, channels, bestLag, epsilon);
            return report;
        }

        private static bool TryOverlap(
            ReadOnlySpan<float> promote,
            ReadOnlySpan<float> manual,
            int lag,
            out ReadOnlySpan<float> p,
            out ReadOnlySpan<float> m)
        {
            // lag > 0: promote starts later → skip promote[0..lag), skip nothing on manual head relative
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

        private static PcmDivergenceReport CompareFixed(
            ReadOnlySpan<float> promote,
            ReadOnlySpan<float> manual,
            int channels,
            int lag,
            float epsilon)
        {
            var n = Math.Min(promote.Length, manual.Length);
            double sumSq = 0;
            float max = 0;
            double pSq = 0;
            double mSq = 0;
            var first = -1;
            float firstP = 0;
            float firstM = 0;

            for (var i = 0; i < n; i++)
            {
                var d = Math.Abs(promote[i] - manual[i]);
                if (d > max)
                {
                    max = d;
                }

                if (first < 0 && d > epsilon)
                {
                    first = i;
                    firstP = promote[i];
                    firstM = manual[i];
                }

                sumSq += d * d;
                pSq += promote[i] * promote[i];
                mSq += manual[i] * manual[i];
            }

            return new PcmDivergenceReport
            {
                MaxAbsDelta = max,
                RmsDelta = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0,
                PromoteRms = n > 0 ? (float)Math.Sqrt(pSq / n) : 0,
                ManualRms = n > 0 ? (float)Math.Sqrt(mSq / n) : 0,
                FirstDivergeIndex = first,
                FirstDivergePromote = firstP,
                FirstDivergeManual = firstM,
                WindowSamples = n,
                ChannelCount = channels,
                BestLagSamples = lag,
            };
        }

        public override string ToString()
        {
            var frame = FirstDivergeIndex < 0 ? -1 : FirstDivergeIndex / Math.Max(1, ChannelCount);
            var ch = FirstDivergeIndex < 0 ? -1 : FirstDivergeIndex % Math.Max(1, ChannelCount);
            return $"maxΔ={MaxAbsDelta:E3} rmsΔ={RmsDelta:E3} promoteRms={PromoteRms:F6} manualRms={ManualRms:F6} "
                + $"lag={BestLagSamples} firstDivergeIdx={FirstDivergeIndex} frame={frame} ch={ch} "
                + $"p={FirstDivergePromote:F6} m={FirstDivergeManual:F6} n={WindowSamples}";
        }
    }
}
