using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Sonora.Audio;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.Devices;
using Sonora.Audio.Windows.DSP;
using Sonora.Audio.Windows.Output;
using Sonora.Audio.Windows.Tests.Fakes;
using Xunit.Abstractions;

namespace Sonora.Audio.Windows.Tests.Diagnostics;

/// <summary>
/// Diagnostic-only: verified gapless promote vs fresh Load vs independent WAV reference.
/// Preserves every attempt; never retries until a PCM match. Does not change playback.
/// </summary>
public sealed class GaplessPromoteIntegrityDiagTests
{
    public const string FixtureAEnv = "SONORA_DIAG_GAPLESS_A";
    public const string FixtureBEnv = "SONORA_DIAG_GAPLESS_B";
    public const string FixtureBFallbackEnv = "SONORA_DIAG_PCM_REF_WAV";
    public const string ListenEnv = "SONORA_DIAG_PROMOTE_LISTEN";
    public const string CaptureDirEnv = "SONORA_DIAG_CAPTURE_DIR";
    public const string EarlyMsEnv = "SONORA_DIAG_PROMOTE_EARLY_MS";
    public const string MidMsEnv = "SONORA_DIAG_PROMOTE_MID_MS";
    public const string WindowMsEnv = "SONORA_DIAG_PROMOTE_WINDOW_MS";

    private static readonly TimeSpan DefaultEarly = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultMid = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(500);
    private const float MeasurementVolume = 1.0f;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ITestOutputHelper _output;

    public GaplessPromoteIntegrityDiagTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Primary: known-bad real A→B pairing. Soft-skips if A/B env paths are missing
    /// (does not fall back to synthetic A).
    /// </summary>
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task KnownBadPair_Promote_Vs_Load_Vs_Reference_DumpAll()
    {
        if (!TryResolveKnownBadPair(out var aPath, out var bPath, out var skipReason))
        {
            _output.WriteLine($"SKIPPED primary promote integrity: {skipReason}");
            return;
        }

        await RunPromoteIntegrityAsync(
            aPath,
            bPath,
            scenarioName: "known-bad-pair",
            requireWavReferenceForB: IsWavPath(bPath));
    }

    /// <summary>
    /// Controlled isolation: synthetic short A + known-bad B. Separate from primary.
    /// </summary>
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task SyntheticA_Isolation_Promote_Vs_Load_Vs_Reference_DumpAll()
    {
        if (!TryResolveB(out var bPath))
        {
            _output.WriteLine(
                $"SKIPPED synthetic-A isolation: set {FixtureBEnv} or {FixtureBFallbackEnv} to a local file.");
            return;
        }

        var aPath = EnsureNamedToneWav("promote-integrity-synth-a-250ms.wav", TimeSpan.FromMilliseconds(250), 220f);
        await RunPromoteIntegrityAsync(
            aPath,
            bPath,
            scenarioName: "synthetic-a-isolation",
            requireWavReferenceForB: IsWavPath(bPath));
    }

    /// <summary>
    /// Focused refinement: multi-window exact-align after verified promote (wav→wav preferred).
    /// Windows: 0–250 ms, 500–750 ms, 1.5 s, 3 s. Dump all; no retry-until-match.
    /// Does not treat moderate NCC + residual as confirmed corruption.
    /// </summary>
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task KnownBadPair_MultiWindow_ExactAlign_DumpAll()
    {
        if (!TryResolveKnownBadPair(out var aPath, out var bPath, out var skipReason))
        {
            _output.WriteLine($"SKIPPED multi-window exact-align: {skipReason}");
            return;
        }

        if (!IsWavPath(bPath))
        {
            _output.WriteLine("SKIPPED multi-window exact-align: B must be PCM16 WAV for independent reference.");
            return;
        }

        await RunMultiWindowExactAlignAsync(aPath, bPath);
    }

    private async Task RunMultiWindowExactAlignAsync(string aPath, string bPath)
    {
        var listen = NormalizeListen(Environment.GetEnvironmentVariable(ListenEnv));
        var dumpRoot = ResolveDumpRoot("multi-window-exact-align");
        var reference = IndependentWavPcm16Reader.Read(bPath);
        var window = TimeSpan.FromMilliseconds(250);
        // Requested starts: 0–250, 500–750, 1.5s, 3s (250 ms windows).
        var starts = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(1500),
            TimeSpan.FromMilliseconds(3000),
        };

        _output.WriteLine("scenario=multi-window-exact-align");
        _output.WriteLine($"A={aPath}");
        _output.WriteLine($"B={bPath}");
        _output.WriteLine($"listenClass={listen} (Clean|Static|Unsure — not inferred from PCM)");
        _output.WriteLine($"referenceB: {reference.FormatDescription}");
        _output.WriteLine($"dumpRoot={dumpRoot}");
        _output.WriteLine($"windows={string.Join(", ", starts.Select(s => $"{s.TotalMilliseconds:F0}+{window.TotalMilliseconds:F0}ms"))}");

        var pipeline = await SnapshotPipelineAsync(aPath, bPath);
        _output.WriteLine($"pipeline (pre-play): {pipeline}");

        var promoteCaps = await CapturePromoteMultiAsync(aPath, bPath, starts, window, pipeline);
        File.WriteAllText(Path.Combine(dumpRoot, "pipeline.json"), JsonSerializer.Serialize(pipeline, JsonOptions));
        File.WriteAllText(
            Path.Combine(dumpRoot, "promote-meta.json"),
            JsonSerializer.Serialize(
                new
                {
                    listenClass = listen,
                    promoteCaps.TransitionId,
                    promoteCaps.PreviousPath,
                    promoteCaps.NextPath,
                    pipeline.MixFormat,
                    pipeline.OutputMode,
                    pipeline.ConvertKind,
                    pipeline.SessionFormat,
                },
                JsonOptions));

        var manualCaps = await CaptureManualMultiAsync(bPath, starts, window);

        var evidenceLines = new List<string>
        {
            $"listenClass={listen}",
            $"transitionId={promoteCaps.TransitionId}",
            pipeline.ToString(),
        };
        var windowReports = new List<object>();

        for (var i = 0; i < starts.Length; i++)
        {
            var start = starts[i];
            var label = $"w{start.TotalMilliseconds:F0}";
            var promote = promoteCaps.Windows[i];
            var manual = manualCaps[i];
            DumpCapture(dumpRoot, $"promote-{label}", promote);
            DumpCapture(dumpRoot, $"manual-{label}", manual);

            _output.WriteLine($"--- window {label} ---");
            _output.WriteLine($"promote: {promote.Notes}");
            _output.WriteLine($"manual: {manual.Notes}");

            if (promote.Format.SampleRate != reference.SampleRate
                || promote.Format.ChannelCount != reference.Channels)
            {
                var msg =
                    $"{label}: SKIPPED reference sample compare — session {FormatString(promote.Format)} "
                    + $"!= reference {reference.SampleRate}/{reference.Channels} (Convert={pipeline.ConvertKind}).";
                _output.WriteLine(msg);
                evidenceLines.Add(msg);
                windowReports.Add(new { window = label, skipped = msg, listenClass = listen });
                continue;
            }

            // Search from 0 through a generous pad past the armed position so lag is absolute in file time.
            var searchStart = TimeSpan.Zero;
            var searchEnd = promote.ArmedAt + window + TimeSpan.FromSeconds(2);
            if (searchEnd < TimeSpan.FromSeconds(5))
            {
                searchEnd = TimeSpan.FromSeconds(5);
            }

            var searchRegion = PcmIntegrityMetrics.SliceByTime(
                reference.Samples,
                reference.SampleRate,
                reference.Channels,
                searchStart,
                searchEnd);

            var exact = PcmIntegrityMetrics.ExactAlignAndClassify(
                promote.Samples,
                searchRegion,
                searchStart,
                promote.ArmedAt,
                promote.RequestedStart,
                promote.Format.SampleRate,
                promote.Format.ChannelCount,
                PcmIntegrityMetrics.ManagedIdentityTolerance);

            // Fresh Load vs same reference (timing context only).
            var manualExact = PcmIntegrityMetrics.ExactAlignAndClassify(
                manual.Samples,
                searchRegion,
                searchStart,
                manual.ArmedAt,
                manual.RequestedStart,
                manual.Format.SampleRate,
                manual.Format.ChannelCount,
                PcmIntegrityMetrics.ManagedIdentityTolerance);

            var vsManual = RealAudioCompare.Compare(
                promote.Samples,
                manual.Samples,
                promote.Format.SampleRate,
                promote.Format.ChannelCount,
                maxLagMs: 150);

            _output.WriteLine($"promote vs reference exact: {exact}");
            _output.WriteLine($"manual vs reference exact: {manualExact}");
            _output.WriteLine(
                $"promote vs manual: suitable={vsManual.Suitable} ncc={vsManual.PeakNcc:F4} "
                + $"match={vsManual.AlignedMatch} inconclusive={vsManual.AlignmentInconclusive} "
                + $"maxΔ={vsManual.MaxAbsDelta:E3} reason={vsManual.Reason}");

            // Timeline mapping clarity
            _output.WriteLine(
                $"{label} timeline: requestedStart={promote.RequestedStart.TotalMilliseconds:F1}ms "
                + $"ArmedAtPosition={promote.ArmedAt.TotalMilliseconds:F1}ms "
                + $"(first captured sample ≈ ArmedAt on player timeline) "
                + $"alignedReferenceOffset={exact.MatchedSourceMs:F1}ms "
                + $"armedΔ={exact.ArmedDeltaMs:F1}ms kind={exact.Kind}");

            evidenceLines.Add($"window={label} listen={listen} promoteExact=({exact})");
            evidenceLines.Add($"window={label} manualExact=({manualExact})");
            evidenceLines.Add(
                $"window={label} vsManual ncc={vsManual.PeakNcc:F4} match={vsManual.AlignedMatch} "
                + $"inconclusive={vsManual.AlignmentInconclusive} maxΔ={vsManual.MaxAbsDelta:E3}");

            windowReports.Add(new
            {
                window = label,
                listenClass = listen,
                requestedStartMs = promote.RequestedStart.TotalMilliseconds,
                armedAtMs = promote.ArmedAt.TotalMilliseconds,
                promoteUnderruns = promote.UnderrunCount,
                manualUnderruns = manual.UnderrunCount,
                promoteExact = exact,
                manualExact = manualExact,
                promoteVsManual = new
                {
                    vsManual.Suitable,
                    vsManual.PeakNcc,
                    vsManual.AlignedMatch,
                    vsManual.AlignmentInconclusive,
                    vsManual.MaxAbsDelta,
                    vsManual.RmsDelta,
                    vsManual.Reason,
                },
            });
        }

        var anyConfirmedCorruption = windowReports.Count > 0 && evidenceLines.Any(l =>
            l.Contains("kind=SampleValueCorruption", StringComparison.Ordinal));
        var anyIdentity = evidenceLines.Any(l => l.Contains("kind=Identity", StringComparison.Ordinal)
            || l.Contains("kind=PureTimeOffset", StringComparison.Ordinal));
        var anyInconclusive = evidenceLines.Any(l =>
            l.Contains("kind=Inconclusive", StringComparison.Ordinal)
            || l.Contains("kind=SilenceUnsuitable", StringComparison.Ordinal));

        string verdict;
        if (string.Equals(listen, "Static", StringComparison.OrdinalIgnoreCase)
            && anyIdentity
            && !anyConfirmedCorruption)
        {
            verdict =
                "listenClass=Static with managed PCM matching reference on one or more windows after exact align. "
                + "Next: continuous WASAPI/output session downstream of managed capture boundary "
                + $"(MixFormat={pipeline.MixFormat}). Do not auto-blame MixFormat resampling.";
        }
        else if (anyConfirmedCorruption)
        {
            verdict =
                "At least one window classified SampleValueCorruption after sample-exact alignment "
                + $"(listenClass={listen}). Inspect firstDiv / competing peaks in evidence.json. No fix applied.";
        }
        else if (anyInconclusive && !anyIdentity)
        {
            verdict =
                "All evaluated windows inconclusive or silent — cannot claim PCM corruption. "
                + $"listenClass={listen}. Dumps preserved.";
        }
        else
        {
            verdict =
                $"See per-window exact-align kinds. listenClass={listen} Convert={pipeline.ConvertKind} "
                + $"identityWindows={anyIdentity} inconclusiveWindows={anyInconclusive} "
                + $"confirmedCorruption={anyConfirmedCorruption}. No fix applied.";
        }

        _output.WriteLine($"VERDICT: {verdict}");
        evidenceLines.Add($"VERDICT: {verdict}");
        File.WriteAllText(Path.Combine(dumpRoot, "evidence.txt"), string.Join(Environment.NewLine, evidenceLines));
        File.WriteAllText(
            Path.Combine(dumpRoot, "evidence.json"),
            JsonSerializer.Serialize(new { listenClass = listen, verdict, windows = windowReports }, JsonOptions));

        Assert.True(promoteCaps.TransitionId > 0);
        Assert.Equal(bPath, promoteCaps.NextPath);
    }

    private async Task<(int TransitionId, string? PreviousPath, string NextPath, List<CaptureWindow> Windows)>
        CapturePromoteMultiAsync(
            string aPath,
            string bPath,
            TimeSpan[] starts,
            TimeSpan window,
            PipelineSnapshot pipeline)
    {
        var outputs = new TrackingWindowsOutputFactory();
        using var engine = CreateEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = MeasurementVolume;

        var transitioned = new TaskCompletionSource<TrackTransitionedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completedWithoutPromote = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTransitioned(object? s, TrackTransitionedEventArgs e) => transitioned.TrySetResult(e);
        void OnCompleted(object? s, EventArgs e) =>
            completedWithoutPromote.TrySetResult(
                $"PlaybackCompleted without TrackTransitioned (nextState={player.NextTrackState}).");

        player.TrackTransitioned += OnTransitioned;
        player.PlaybackCompleted += OnCompleted;

        await player.LoadAsync(aPath);
        await player.PreloadNextAsync(bPath);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        Assert.Equal(bPath, player.NextTrackPath);
        _output.WriteLine($"pre-play NextTrackState={player.NextTrackState} next={player.NextTrackPath}");

        player.Play();

        var finished = await Task.WhenAny(
            transitioned.Task,
            completedWithoutPromote.Task,
            Task.Delay(TimeSpan.FromSeconds(Math.Max(120, EstimateDurationSeconds(aPath) + 60))));

        if (finished == completedWithoutPromote.Task)
        {
            throw new InvalidOperationException(await completedWithoutPromote.Task);
        }

        if (finished != transitioned.Task || !transitioned.Task.IsCompletedSuccessfully)
        {
            throw new TimeoutException("Timed out waiting for TrackTransitioned.");
        }

        var args = await transitioned.Task;
        player.TrackTransitioned -= OnTransitioned;
        player.PlaybackCompleted -= OnCompleted;
        Assert.Equal(bPath, player.SourcePath);
        _output.WriteLine(
            $"TrackTransitioned id={args.TransitionId} prev={Path.GetFileName(args.PreviousPath)} "
            + $"next={Path.GetFileName(args.NextPath)}");

        var windows = new List<CaptureWindow>(starts.Length);
        foreach (var start in starts)
        {
            windows.Add(await CaptureAtAsync(player, outputs, start, window, $"promote-w{start.TotalMilliseconds:F0}"));
        }

        pipeline.MixFormat = TryReadMixFormat(outputs.LastWasapi?.EndpointId);
        pipeline.OutputMode = outputs.LastWasapi?.Mode.ToString() ?? "?";
        player.Stop();

        return (args.TransitionId, args.PreviousPath, args.NextPath ?? bPath, windows);
    }

    private async Task<List<CaptureWindow>> CaptureManualMultiAsync(
        string bPath,
        TimeSpan[] starts,
        TimeSpan window)
    {
        var outputs = new TrackingWindowsOutputFactory();
        using var engine = CreateEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = MeasurementVolume;
        await player.LoadAsync(bPath);
        player.Play();

        var windows = new List<CaptureWindow>(starts.Length);
        foreach (var start in starts)
        {
            windows.Add(await CaptureAtAsync(player, outputs, start, window, $"manual-w{start.TotalMilliseconds:F0}"));
        }

        player.Stop();
        return windows;
    }

    private async Task RunPromoteIntegrityAsync(
        string aPath,
        string bPath,
        string scenarioName,
        bool requireWavReferenceForB)
    {
        var earlyStart = ReadTimeMs(EarlyMsEnv, DefaultEarly);
        var midStart = ReadTimeMs(MidMsEnv, DefaultMid);
        var window = ReadTimeMs(WindowMsEnv, DefaultWindow);
        var listen = NormalizeListen(Environment.GetEnvironmentVariable(ListenEnv));
        var dumpRoot = ResolveDumpRoot(scenarioName);

        _output.WriteLine($"scenario={scenarioName}");
        _output.WriteLine($"A={aPath}");
        _output.WriteLine($"B={bPath}");
        _output.WriteLine($"listenClass={listen} (Clean|Static|Unsure — human observation; not inferred from PCM)");
        _output.WriteLine($"early={earlyStart.TotalMilliseconds:F0}ms mid={midStart.TotalMilliseconds:F0}ms window={window.TotalMilliseconds:F0}ms");
        _output.WriteLine($"measurementVolume={MeasurementVolume} (automated only)");
        _output.WriteLine($"dumpRoot={dumpRoot}");

        IndependentWavPcm16Reader.Result? reference = null;
        if (requireWavReferenceForB)
        {
            reference = IndependentWavPcm16Reader.Read(bPath);
            _output.WriteLine($"referenceB: {reference.FormatDescription}");
        }
        else
        {
            _output.WriteLine(
                "referenceB: unavailable (B is not PCM16 WAV) — promote vs Load compare only; "
                + "no independent sample-identity vs source file.");
        }

        var pipeline = await SnapshotPipelineAsync(aPath, bPath);
        _output.WriteLine($"pipeline (pre-play): {pipeline}");

        // --- Single promote session: early + mid (no retries) ---
        var promoteAttempt = await CapturePromoteWindowsAsync(
            aPath,
            bPath,
            earlyStart,
            midStart,
            window,
            pipeline);

        File.WriteAllText(Path.Combine(dumpRoot, "pipeline.json"), JsonSerializer.Serialize(pipeline, JsonOptions));
        DumpCapture(dumpRoot, "promote-early", promoteAttempt.Early);
        DumpCapture(dumpRoot, "promote-mid", promoteAttempt.Mid);
        File.WriteAllText(
            Path.Combine(dumpRoot, "promote-meta.json"),
            JsonSerializer.Serialize(promoteAttempt.ToMeta(listen), JsonOptions));

        _output.WriteLine($"promote verified TrackTransitioned id={promoteAttempt.TransitionId}");
        _output.WriteLine($"promote early: {promoteAttempt.Early.Notes}");
        _output.WriteLine($"promote mid: {promoteAttempt.Mid.Notes}");

        // --- Single manual Load session: early + mid (no retries) ---
        var manualAttempt = await CaptureManualWindowsAsync(bPath, earlyStart, midStart, window);
        DumpCapture(dumpRoot, "manual-early", manualAttempt.Early);
        DumpCapture(dumpRoot, "manual-mid", manualAttempt.Mid);
        File.WriteAllText(
            Path.Combine(dumpRoot, "manual-meta.json"),
            JsonSerializer.Serialize(manualAttempt.ToMeta(listen: "n/a-load"), JsonOptions));

        _output.WriteLine($"manual early: {manualAttempt.Early.Notes}");
        _output.WriteLine($"manual mid: {manualAttempt.Mid.Notes}");

        var evidence = new List<WindowEvidence>
        {
            EvaluateWindow(
                "promote-early",
                promoteAttempt.Early,
                manualAttempt.Early,
                reference,
                listen,
                pipeline),
            EvaluateWindow(
                "promote-mid",
                promoteAttempt.Mid,
                manualAttempt.Mid,
                reference,
                listen,
                pipeline),
        };

        var sb = new StringBuilder();
        sb.AppendLine($"scenario={scenarioName}");
        sb.AppendLine($"listenClass={listen}");
        sb.AppendLine($"transitionId={promoteAttempt.TransitionId}");
        sb.AppendLine(pipeline.ToString());
        foreach (var e in evidence)
        {
            _output.WriteLine(e.ToString());
            sb.AppendLine(e.ToString());
        }

        var verdict = BuildVerdict(evidence, listen, pipeline);
        _output.WriteLine($"VERDICT: {verdict}");
        sb.AppendLine($"VERDICT: {verdict}");
        File.WriteAllText(Path.Combine(dumpRoot, "evidence.txt"), sb.ToString());
        File.WriteAllText(
            Path.Combine(dumpRoot, "evidence.json"),
            JsonSerializer.Serialize(
                new
                {
                    scenario = scenarioName,
                    listenClass = listen,
                    transitionId = promoteAttempt.TransitionId,
                    pipeline,
                    windows = evidence,
                    verdict,
                },
                JsonOptions));

        // Soft structural asserts: path must be real promote. PCM mismatches are evidence, not assert-until-match.
        Assert.True(promoteAttempt.TransitionId > 0, "Expected verified TrackTransitioned.");
        Assert.Equal(bPath, promoteAttempt.NextPath);
    }

    private WindowEvidence EvaluateWindow(
        string name,
        CaptureWindow promote,
        CaptureWindow manual,
        IndependentWavPcm16Reader.Result? reference,
        string listen,
        PipelineSnapshot pipeline)
    {
        var ev = new WindowEvidence
        {
            Window = name,
            ListenClass = listen,
            PromoteUnderruns = promote.UnderrunCount,
            ManualUnderruns = manual.UnderrunCount,
            PromoteArmedMs = promote.ArmedAt.TotalMilliseconds,
            ManualArmedMs = manual.ArmedAt.TotalMilliseconds,
            PromoteFormat = FormatString(promote.Format),
            ManualFormat = FormatString(manual.Format),
            SessionFormat = pipeline.SessionFormat,
            MixFormat = pipeline.MixFormat,
            ConvertKind = pipeline.ConvertKind,
        };

        if (promote.Format.SampleRate != manual.Format.SampleRate
            || promote.Format.ChannelCount != manual.Format.ChannelCount)
        {
            ev.CaptureValidity = "invalid-format-mismatch-promote-vs-manual";
            ev.SampleFidelity = "not-evaluated";
            ev.Notes =
                $"Promote session {FormatString(promote.Format)} vs manual {FormatString(manual.Format)} — "
                + "do not index-compare.";
            return ev;
        }

        // Lag-tolerant promote vs manual (same rates). Failed correlation ≠ corrupted PCM.
        var manualReport = RealAudioCompare.Compare(
            promote.Samples,
            manual.Samples,
            promote.Format.SampleRate,
            promote.Format.ChannelCount,
            maxLagMs: 150);
        var vsManual = new PcmIntegrityMetrics.AlignReport
        {
            Suitable = manualReport.Suitable,
            Reason = manualReport.Reason,
            AlignmentInconclusive = manualReport.AlignmentInconclusive,
            SampleIdentity = manualReport.AlignedMatch,
            PeakNcc = manualReport.PeakNcc,
            BestLagSamples = manualReport.BestLagSamples,
            BestLagMs = manualReport.BestLagMs,
            MaxAbsDelta = manualReport.MaxAbsDelta,
            RmsDelta = manualReport.RmsDelta,
            FirstDivergeIndex = manualReport.FirstDivergeIndex,
            ComparedSamples = manualReport.ComparedSamples,
            CapturePeak = manualReport.PromotePeak,
            ReferencePeak = manualReport.ManualPeak,
            CaptureRms = manualReport.PromoteRms,
            ReferenceRms = manualReport.ManualRms,
            ContentWindowVerified = manualReport.Suitable && !manualReport.AlignmentInconclusive,
            WrongWindowNcc = 0,
        };
        ev.PromoteVsManual = vsManual;

        if (!vsManual.Suitable || vsManual.AlignmentInconclusive)
        {
            ev.CaptureValidity = "inconclusive-promote-vs-manual-alignment";
            ev.SampleFidelity = "not-evaluated";
            ev.Notes =
                "Failed correlation between promote and manual captures is NOT automatic proof of corrupted PCM. "
                + vsManual;
        }
        else
        {
            ev.CaptureValidity = "valid-promote-vs-manual-align";
            ev.SampleFidelity = vsManual.SampleIdentity ? "promote-equals-manual" : "promote-differs-from-manual";
        }

        if (reference is null)
        {
            ev.Notes = (ev.Notes + " No WAV reference for B.").Trim();
            return ev;
        }

        if (promote.Format.SampleRate != reference.SampleRate
            || promote.Format.ChannelCount != reference.Channels)
        {
            ev.ReferenceCompare = "skipped-rate-or-channel-mismatch";
            ev.Notes = (ev.Notes
                + $" Session {FormatString(promote.Format)} != reference {reference.SampleRate}/{reference.Channels}; "
                + "resampling/channel conversion present — no direct sample-index compare to WAV reference.").Trim();
            return ev;
        }

        // Reference neighborhood around armed position (±2s), content verify with +5s wrong window.
        var searchPad = TimeSpan.FromSeconds(2);
        var searchStart = promote.ArmedAt - searchPad;
        if (searchStart < TimeSpan.Zero)
        {
            searchStart = TimeSpan.Zero;
        }

        var searchRegion = PcmIntegrityMetrics.SliceByTime(
            reference.Samples,
            reference.SampleRate,
            reference.Channels,
            searchStart,
            TimeSpan.FromMilliseconds(promote.WindowMs) + searchPad + searchPad);

        var wrong = PcmIntegrityMetrics.SliceByTime(
            reference.Samples,
            reference.SampleRate,
            reference.Channels,
            promote.ArmedAt + TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(promote.WindowMs));

        var vsRef = PcmIntegrityMetrics.FindInReferenceAndCompare(
            promote.Samples,
            searchRegion,
            wrong,
            promote.Format.SampleRate,
            promote.Format.ChannelCount,
            identityTolerance: PcmIntegrityMetrics.ManagedIdentityTolerance);

        var matchedSource =
            searchStart
            + TimeSpan.FromSeconds(
                vsRef.BestLagSamples / (double)(promote.Format.SampleRate * promote.Format.ChannelCount));
        var armedDeltaMs = (matchedSource - promote.ArmedAt).TotalMilliseconds;
        var contentNearArmed = Math.Abs(armedDeltaMs) <= searchPad.TotalMilliseconds + 250;
        if (vsRef.Suitable && !vsRef.AlignmentInconclusive && !contentNearArmed)
        {
            vsRef = vsRef with
            {
                ContentWindowVerified = false,
                Reason =
                    $"Aligned content at {matchedSource.TotalMilliseconds:F0}ms vs armed "
                    + $"{promote.ArmedAt.TotalMilliseconds:F0}ms (Δ {armedDeltaMs:F0}ms).",
            };
        }

        ev.PromoteVsReference = vsRef;
        ev.ArmedDeltaMs = armedDeltaMs;

        if (!vsRef.Suitable || vsRef.AlignmentInconclusive || !vsRef.ContentWindowVerified)
        {
            // Capture validity vs fidelity distinction
            if (ev.CaptureValidity.StartsWith("valid", StringComparison.Ordinal))
            {
                ev.CaptureValidity += "+ref-align-inconclusive";
            }
            else if (ev.CaptureValidity.StartsWith("inconclusive", StringComparison.Ordinal))
            {
                // keep
            }
            else
            {
                ev.CaptureValidity = "inconclusive-promote-vs-reference-alignment";
            }

            ev.Notes = (ev.Notes
                + " Reference alignment inconclusive — not automatic evidence of PCM corruption. "
                + vsRef).Trim();
        }
        else if (vsRef.SampleIdentity)
        {
            ev.SampleFidelity = AppendFidelity(ev.SampleFidelity, "promote-equals-reference");
        }
        else
        {
            ev.SampleFidelity = AppendFidelity(ev.SampleFidelity, "promote-differs-from-reference");
        }

        return ev;
    }

    private static string AppendFidelity(string current, string add) =>
        string.IsNullOrEmpty(current) || current == "not-evaluated" ? add : current + ";" + add;

    private string BuildVerdict(List<WindowEvidence> evidence, string listen, PipelineSnapshot pipeline)
    {
        var anyRefMatch = evidence.Any(e =>
            e.PromoteVsReference?.SampleIdentity == true
            && e.PromoteVsReference.ContentWindowVerified
            && !e.PromoteVsReference.AlignmentInconclusive);

        var anyRefDiff = evidence.Any(e =>
            e.PromoteVsReference is { Suitable: true, AlignmentInconclusive: false, ContentWindowVerified: true, SampleIdentity: false });

        var anyManualDiff = evidence.Any(e =>
            e.PromoteVsManual is { Suitable: true, AlignmentInconclusive: false, SampleIdentity: false });

        var anyInconclusive = evidence.Any(e =>
            e.CaptureValidity.Contains("inconclusive", StringComparison.OrdinalIgnoreCase));

        if (string.Equals(listen, "Static", StringComparison.OrdinalIgnoreCase) && anyRefMatch && !anyRefDiff)
        {
            return
                "Managed PCM matched WAV reference during listenClass=Static on verified promote. "
                + "Next: investigate persistent WASAPI/output session continuity across promote "
                + $"(mode/session={pipeline.SessionFormat}, MixFormat={pipeline.MixFormat}). "
                + "Do not automatically blame Windows MixFormat resampling.";
        }

        var earlyRefDiff = evidence.Any(e =>
            e.Window.Contains("early", StringComparison.OrdinalIgnoreCase)
            && e.PromoteVsReference is { Suitable: true, AlignmentInconclusive: false, ContentWindowVerified: true, SampleIdentity: false });
        var midRefMatch = evidence.Any(e =>
            e.Window.Contains("mid", StringComparison.OrdinalIgnoreCase)
            && e.PromoteVsReference?.SampleIdentity == true);
        var midRefDiff = evidence.Any(e =>
            e.Window.Contains("mid", StringComparison.OrdinalIgnoreCase)
            && e.PromoteVsReference is { Suitable: true, AlignmentInconclusive: false, ContentWindowVerified: true, SampleIdentity: false });

        if (earlyRefDiff && midRefMatch && !midRefDiff)
        {
            return
                "Early promote window diverges from WAV reference after valid content alignment; "
                + "mid-track promote matches reference (sample identity). "
                + $"Convert={pipeline.ConvertKind}. First divergent stage candidate: promote handoff / early warm-pipeline "
                + "(not sustained mid-track decode). listenClass={listen}. No fix applied.";
        }

        if (anyRefDiff || anyManualDiff)
        {
            return
                "Managed PCM diverged from reference and/or manual Load after valid alignment on verified promote. "
                + $"First divergent stage candidate: preload/promote pipeline (Convert={pipeline.ConvertKind}). "
                + "Inspect early vs mid window fidelity in evidence.json. No fix applied.";
        }

        if (anyInconclusive)
        {
            return
                "Capture alignment inconclusive for one or more windows — cannot claim PCM corruption. "
                + $"listenClass={listen}. Preserve dumps under capture dir; re-listen / re-capture as new attempts "
                + "(do not retry-until-match within a single attempt).";
        }

        if (anyRefMatch)
        {
            return
                $"Managed PCM matched reference on verified promote (listenClass={listen}). "
                + "If audible static was Unsure, confirm listenClass with TestPlayer before blaming post-managed path. "
                + $"MixFormat={pipeline.MixFormat}.";
        }

        return $"See per-window evidence. listenClass={listen} Convert={pipeline.ConvertKind}.";
    }

    private async Task<PromoteAttempt> CapturePromoteWindowsAsync(
        string aPath,
        string bPath,
        TimeSpan earlyStart,
        TimeSpan midStart,
        TimeSpan window,
        PipelineSnapshot pipeline)
    {
        var outputs = new TrackingWindowsOutputFactory();
        using var engine = CreateEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = MeasurementVolume;

        var transitioned = new TaskCompletionSource<TrackTransitionedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completedWithoutPromote = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTransitioned(object? s, TrackTransitionedEventArgs e) => transitioned.TrySetResult(e);
        void OnCompleted(object? s, EventArgs e) =>
            completedWithoutPromote.TrySetResult(
                $"PlaybackCompleted without TrackTransitioned (nextState={player.NextTrackState}).");

        player.TrackTransitioned += OnTransitioned;
        player.PlaybackCompleted += OnCompleted;

        await player.LoadAsync(aPath);
        await player.PreloadNextAsync(bPath);

        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        Assert.Equal(bPath, player.NextTrackPath);
        _output.WriteLine($"pre-play NextTrackState={player.NextTrackState} next={player.NextTrackPath}");

        player.Play();

        var finished = await Task.WhenAny(
            transitioned.Task,
            completedWithoutPromote.Task,
            Task.Delay(TimeSpan.FromSeconds(Math.Max(120, EstimateDurationSeconds(aPath) + 60))));

        if (finished == completedWithoutPromote.Task)
        {
            throw new InvalidOperationException(await completedWithoutPromote.Task);
        }

        if (finished != transitioned.Task || !transitioned.Task.IsCompletedSuccessfully)
        {
            throw new TimeoutException("Timed out waiting for TrackTransitioned (verified promote required).");
        }

        var args = await transitioned.Task;
        player.TrackTransitioned -= OnTransitioned;
        player.PlaybackCompleted -= OnCompleted;

        Assert.Equal(bPath, player.SourcePath);
        _output.WriteLine(
            $"TrackTransitioned id={args.TransitionId} prev={Path.GetFileName(args.PreviousPath)} "
            + $"next={Path.GetFileName(args.NextPath)}");

        // Early window first (near start of B), then mid — single promote session, both dumps kept.
        var early = await CaptureAtAsync(player, outputs, earlyStart, window, "promote-early");
        var mid = await CaptureAtAsync(player, outputs, midStart, window, "promote-mid");

        var mix = TryReadMixFormat(outputs.LastWasapi?.EndpointId);
        pipeline.MixFormat = mix;
        pipeline.OutputMode = outputs.LastWasapi?.Mode.ToString() ?? pipeline.OutputMode;

        player.Stop();

        return new PromoteAttempt
        {
            TransitionId = args.TransitionId,
            PreviousPath = args.PreviousPath,
            NextPath = args.NextPath ?? bPath,
            Early = early,
            Mid = mid,
            MixFormat = mix,
            OutputMode = outputs.LastWasapi?.Mode.ToString() ?? "?",
        };
    }

    private async Task<ManualAttempt> CaptureManualWindowsAsync(
        string bPath,
        TimeSpan earlyStart,
        TimeSpan midStart,
        TimeSpan window)
    {
        var outputs = new TrackingWindowsOutputFactory();
        using var engine = CreateEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = MeasurementVolume;

        await player.LoadAsync(bPath);
        player.Play();

        var early = await CaptureAtAsync(player, outputs, earlyStart, window, "manual-early");
        var mid = await CaptureAtAsync(player, outputs, midStart, window, "manual-mid");
        player.Stop();

        return new ManualAttempt { Early = early, Mid = mid };
    }

    private static async Task<CaptureWindow> CaptureAtAsync(
        AudioPlayer player,
        TrackingWindowsOutputFactory outputs,
        TimeSpan start,
        TimeSpan window,
        string label)
    {
        var captured = await ManagedOutputPositionCapture.CaptureWindowAtPositionAsync(
            player,
            () => outputs.LastWasapi,
            start,
            window,
            timeout: TimeSpan.FromSeconds(start.TotalSeconds + window.TotalSeconds + 45));

        return new CaptureWindow
        {
            Label = label,
            Samples = captured.Samples,
            Format = captured.Format,
            ArmedAt = captured.ArmedAtPosition,
            RequestedStart = captured.RequestedStart,
            WindowMs = window.TotalMilliseconds,
            UnderrunCount = captured.UnderrunCount,
            SilenceReason = captured.LastSilenceReason,
            OutputMode = captured.OutputMode,
            SourcePath = captured.SourcePath,
            TransitionId = captured.TransitionId,
            Notes =
                $"{label} armedAt={captured.ArmedAtPosition.TotalMilliseconds:F1}ms "
                + $"n={captured.Samples.Length} underruns={captured.UnderrunCount} "
                + $"silence={captured.LastSilenceReason} mode={captured.OutputMode} "
                + $"fmt={FormatString(captured.Format)}",
        };
    }

    private async Task<PipelineSnapshot> SnapshotPipelineAsync(string aPath, string bPath)
    {
        var factory = new WindowsAudioDecoderFactory();
        using var decA = await factory.CreateAsync(aPath);
        using var decB = await factory.CreateAsync(bPath);
        var session = decA.Format; // Load A locks session to A's native format
        var nativeB = decB.Format;

        string convertKind;
        if (nativeB.SampleRate == session.SampleRate && nativeB.ChannelCount == session.ChannelCount)
        {
            convertKind = "identity";
        }
        else if (nativeB.SampleRate == session.SampleRate)
        {
            convertKind = "channel-mix";
        }
        else
        {
            convertKind = "wdl-resample";
        }

        return new PipelineSnapshot
        {
            SourceA = aPath,
            SourceB = bPath,
            NativeAFormat = FormatString(decA.Format),
            NativeBFormat = FormatString(nativeB),
            SessionFormat = FormatString(session),
            ConvertKind = convertKind,
            PrimeMs = 50,
            BufferMs = 500,
            EffectsBypass = true,
            EffectsResetOnPromote = false,
            MeasurementVolume = MeasurementVolume,
            MixFormat = "(captured after play)",
            OutputMode = "(captured after play)",
        };
    }

    private static void DumpCapture(string dir, string name, CaptureWindow window)
    {
        File.WriteAllBytes(Path.Combine(dir, $"{name}.f32"), FloatBytes(window.Samples));
        File.WriteAllText(
            Path.Combine(dir, $"{name}.txt"),
            window.Notes + Environment.NewLine);
    }

    private static byte[] FloatBytes(float[] samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(float))];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private string ResolveDumpRoot(string scenario)
    {
        var root = Environment.GetEnvironmentVariable(CaptureDirEnv);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "sonora-gapless-promote-integrity");
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(root, $"{scenario}-{stamp}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool TryResolveKnownBadPair(out string aPath, out string bPath, out string skipReason)
    {
        aPath = Environment.GetEnvironmentVariable(FixtureAEnv) ?? "";
        bPath = Environment.GetEnvironmentVariable(FixtureBEnv)
            ?? Environment.GetEnvironmentVariable(FixtureBFallbackEnv)
            ?? "";

        if (string.IsNullOrWhiteSpace(aPath) || !File.Exists(aPath))
        {
            skipReason =
                $"set {FixtureAEnv} to a known-bad local file (primary does not use synthetic A).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(bPath) || !File.Exists(bPath))
        {
            skipReason = $"set {FixtureBEnv} or {FixtureBFallbackEnv} to a known-bad local file.";
            return false;
        }

        skipReason = "";
        return true;
    }

    private static bool TryResolveB(out string bPath)
    {
        bPath = Environment.GetEnvironmentVariable(FixtureBEnv)
            ?? Environment.GetEnvironmentVariable(FixtureBFallbackEnv)
            ?? "";
        return !string.IsNullOrWhiteSpace(bPath) && File.Exists(bPath);
    }

    private static bool IsWavPath(string path) =>
        path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeListen(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Unsure";
        }

        raw = raw.Trim();
        if (raw.Equals("Clean", StringComparison.OrdinalIgnoreCase))
        {
            return "Clean";
        }

        if (raw.Equals("Static", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Distorted", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("promote_distorted_load_clean", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("both_distorted", StringComparison.OrdinalIgnoreCase))
        {
            return "Static";
        }

        if (raw.Equals("Unsure", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("inconclusive", StringComparison.OrdinalIgnoreCase))
        {
            return "Unsure";
        }

        return "Unsure";
    }

    private static string FormatString(AudioFormat f) =>
        $"{f.SampleRate}Hz/{f.ChannelCount}ch/{f.SampleFormat}";

    private static string TryReadMixFormat(string? endpointId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = string.IsNullOrWhiteSpace(endpointId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(endpointId);
            using var client = device.CreateAudioClient();
            var mix = client.MixFormat;
            return $"{mix.SampleRate}Hz {mix.Channels}ch encoding={mix.Encoding} bits={mix.BitsPerSample}";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name})";
        }
    }

    private static double EstimateDurationSeconds(string path)
    {
        try
        {
            if (IsWavPath(path))
            {
                return IndependentWavPcm16Reader.Read(path).Duration.TotalSeconds;
            }
        }
        catch
        {
            // fall through
        }

        return 60;
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

    private sealed class CaptureWindow
    {
        public required string Label { get; init; }
        public required float[] Samples { get; init; }
        public required AudioFormat Format { get; init; }
        public required TimeSpan ArmedAt { get; init; }
        public required TimeSpan RequestedStart { get; init; }
        public required double WindowMs { get; init; }
        public required long UnderrunCount { get; init; }
        public required string SilenceReason { get; init; }
        public required string OutputMode { get; init; }
        public string? SourcePath { get; init; }
        public int TransitionId { get; init; }
        public required string Notes { get; init; }
    }

    private sealed class PromoteAttempt
    {
        public required int TransitionId { get; init; }
        public string? PreviousPath { get; init; }
        public required string NextPath { get; init; }
        public required CaptureWindow Early { get; init; }
        public required CaptureWindow Mid { get; init; }
        public required string MixFormat { get; init; }
        public required string OutputMode { get; init; }

        public object ToMeta(string listen) => new
        {
            listenClass = listen,
            TransitionId,
            PreviousPath,
            NextPath,
            MixFormat,
            OutputMode,
            early = Early.Notes,
            mid = Mid.Notes,
        };
    }

    private sealed class ManualAttempt
    {
        public required CaptureWindow Early { get; init; }
        public required CaptureWindow Mid { get; init; }

        public object ToMeta(string listen) => new
        {
            listenClass = listen,
            early = Early.Notes,
            mid = Mid.Notes,
        };
    }

    private sealed class PipelineSnapshot
    {
        public required string SourceA { get; init; }
        public required string SourceB { get; init; }
        public required string NativeAFormat { get; init; }
        public required string NativeBFormat { get; init; }
        public required string SessionFormat { get; init; }
        public required string ConvertKind { get; init; }
        public required int PrimeMs { get; init; }
        public required int BufferMs { get; init; }
        public required bool EffectsBypass { get; init; }
        public required bool EffectsResetOnPromote { get; init; }
        public required float MeasurementVolume { get; init; }
        public string MixFormat { get; set; } = "";
        public string OutputMode { get; set; } = "";

        public override string ToString() =>
            $"pipeline nativeA={NativeAFormat} nativeB={NativeBFormat} session={SessionFormat} "
            + $"convert={ConvertKind} prime={PrimeMs}ms buffer={BufferMs}ms bypass={EffectsBypass} "
            + $"effectsResetOnPromote={EffectsResetOnPromote} mode={OutputMode} mix={MixFormat}";
    }

    private sealed class WindowEvidence
    {
        public required string Window { get; init; }
        public required string ListenClass { get; init; }
        public string CaptureValidity { get; set; } = "unknown";
        public string SampleFidelity { get; set; } = "not-evaluated";
        public string? ReferenceCompare { get; set; }
        public string? Notes { get; set; }
        public PcmIntegrityMetrics.AlignReport? PromoteVsManual { get; set; }
        public PcmIntegrityMetrics.AlignReport? PromoteVsReference { get; set; }
        public double ArmedDeltaMs { get; set; }
        public long PromoteUnderruns { get; init; }
        public long ManualUnderruns { get; init; }
        public double PromoteArmedMs { get; init; }
        public double ManualArmedMs { get; init; }
        public string? PromoteFormat { get; init; }
        public string? ManualFormat { get; init; }
        public string? SessionFormat { get; init; }
        public string? MixFormat { get; init; }
        public string? ConvertKind { get; init; }

        public override string ToString() =>
            $"window={Window} listen={ListenClass} validity={CaptureValidity} fidelity={SampleFidelity} "
            + $"armedΔ={ArmedDeltaMs:F1}ms underrunsP/M={PromoteUnderruns}/{ManualUnderruns} "
            + $"vsManual=({PromoteVsManual}) vsRef=({PromoteVsReference}) notes={Notes}";
    }
}
