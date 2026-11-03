using System.Globalization;
using System.Text;
using System.Text.Json;
using Sonora.Audio;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Output;
using Sonora.TestPlayer.Controllers;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Diagnostics;

/// <summary>
/// Interactive listening A/B: verified natural promote vs fresh Load, with human Clean/Static/Unsure
/// observations tied to each attempt. Diagnostic-only; does not change Sonora playback lifecycle APIs
/// beyond calling them. File I/O stays off the WASAPI callback.
/// </summary>
public sealed class ListeningAbTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly TimeSpan[] DefaultCaptureStarts =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromSeconds(3),
    ];

    private static readonly TimeSpan CaptureWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultListenOnB = TimeSpan.FromSeconds(8);

    private readonly PlaybackController _playback;
    private readonly DiagnosticEventStore _store;
    private readonly ListeningObservationGate _observations = new();
    private int _busy;

    public ListeningAbTestService(PlaybackController playback, DiagnosticEventStore store)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ListeningObservationGate Observations => _observations;

    /// <summary>Optional UI push for phases / observation prompts.</summary>
    public Action<string, object?>? PhaseChanged { get; set; }

    public async Task<object> RunAsync(
        string trackAPath,
        string trackBPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackAPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackBPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!File.Exists(trackAPath) || !File.Exists(trackBPath))
        {
            throw new FileNotFoundException("Track A and B must exist.");
        }

        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            throw new InvalidOperationException("A listening A/B test is already in progress.");
        }

        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var expandedOut = Environment.ExpandEnvironmentVariables(outputDirectory.Trim());
            var root = Path.Combine(expandedOut, $"listening-ab-{stamp}");
            Directory.CreateDirectory(root);

            ListeningWavReference.Result? reference = null;
            string? referenceError = null;
            if (ListeningWavReference.TryReadPcm16(trackBPath, out var refResult, out referenceError))
            {
                reference = refResult;
            }

            var volume = _playback.Player?.Volume ?? 0.17f;
            volume = Math.Clamp(volume, 0.01f, 1f);

            Report("starting", $"Listening A/B root={root}; volume={volume:F3} (not forced to 1.0).");

            var promote = await RunPromoteScenarioAsync(
                    trackAPath,
                    trackBPath,
                    root,
                    volume,
                    reference,
                    cancellationToken)
                .ConfigureAwait(false);

            var load = await RunFreshLoadScenarioAsync(
                    trackBPath,
                    root,
                    volume,
                    reference,
                    cancellationToken)
                .ConfigureAwait(false);

            var summaryLine =
                ListeningPathRules.SummarizePath(
                    "Natural promotion",
                    promote.TrackTransitionedFired,
                    promote.TransitionId,
                    promote.Observation.Classification,
                    promote.PcmSummary)
                + Environment.NewLine
                + ListeningPathRules.SummarizePath(
                    "Fresh Load",
                    load.TrackTransitionedFired,
                    load.TransitionId,
                    load.Observation.Classification,
                    load.PcmSummary);

            var caution =
                promote.Observation.Classification == ListeningClassification.Static
                && load.Observation.Classification == ListeningClassification.Clean
                && promote.PcmSummary.Contains("exact", StringComparison.OrdinalIgnoreCase)
                && load.PcmSummary.Contains("exact", StringComparison.OrdinalIgnoreCase)
                    ? "Audible difference with matching managed PCM suggests investigating the output path "
                      + "downstream of the managed capture boundary — does not independently prove the cause."
                    : null;

            var report = new
            {
                trackA = trackAPath,
                trackB = trackBPath,
                listeningVolume = volume,
                referenceFormat = reference?.FormatDescription,
                referenceError,
                dumpRoot = root,
                promote,
                load,
                summaryLine,
                caution,
                generatedAtUtc = DateTimeOffset.UtcNow,
            };

            await File.WriteAllTextAsync(
                    Path.Combine(root, "report.json"),
                    JsonSerializer.Serialize(report, JsonOptions),
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(root, "summary.txt"),
                    summaryLine
                    + (caution is null ? "" : Environment.NewLine + Environment.NewLine + caution)
                    + Environment.NewLine,
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);

            Report("completed", summaryLine.Replace(Environment.NewLine, " | "));
            return report;
        }
        catch (Exception ex)
        {
            _observations.CancelPending();
            Report("failed", ex.Message);
            throw;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public bool SubmitObservation(string attemptId, string classification, string? notes = null)
    {
        var parsed = ParseClassification(classification);
        return _observations.TrySubmit(attemptId, parsed, notes);
    }

    private async Task<ScenarioResult> RunPromoteScenarioAsync(
        string trackA,
        string trackB,
        string root,
        float volume,
        ListeningWavReference.Result? reference,
        CancellationToken cancellationToken)
    {
        var attemptId = "promote-" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(root, "promote");
        Directory.CreateDirectory(dir);

        Report("promote.prepare", "Preparing queue A→B with gapless enabled.");
        EnsureVolume(volume);
        _playback.ClearQueue();
        await _playback.AddFilesAsync([trackA, trackB], cancellationToken).ConfigureAwait(false);
        _playback.SetGaplessEnabled(true);
        await _playback.LoadAsync(trackA, cancellationToken).ConfigureAwait(false);

        var player = RequirePlayer();
        await _playback.PreloadNextAsync(cancellationToken).ConfigureAwait(false);
        await WaitReadyAsync(player, trackB, cancellationToken).ConfigureAwait(false);

        var transitioned = new TaskCompletionSource<TrackTransitionedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTransitioned(object? s, TrackTransitionedEventArgs e) => transitioned.TrySetResult(e);
        player.TrackTransitioned += OnTransitioned;

        Report("promote.play", "Playing A; waiting for verified TrackTransitioned into B.");
        await _playback.PlayAsync(cancellationToken).ConfigureAwait(false);

        TrackTransitionedEventArgs args;
        try
        {
            var timeout = TransitionWaitTimeout(player);
            var finished = await Task.WhenAny(transitioned.Task, Task.Delay(timeout, cancellationToken))
                .ConfigureAwait(false);
            if (finished != transitioned.Task || !transitioned.Task.IsCompletedSuccessfully)
            {
                throw new TimeoutException(
                    "Timed out waiting for TrackTransitioned. Enable gapless and ensure NextTrackState=Ready.");
            }

            args = await transitioned.Task.ConfigureAwait(false);
        }
        finally
        {
            player.TrackTransitioned -= OnTransitioned;
        }

        if (!ListeningPathRules.IsVerifiedPromote(true, args.TransitionId, trackB, player.SourcePath))
        {
            throw new InvalidOperationException(
                $"Promote path verification failed (id={args.TransitionId}, source={player.SourcePath}).");
        }

        Report(
            "promote.transitioned",
            $"TrackTransitioned id={args.TransitionId} → {Path.GetFileName(args.NextPath)}.");

        var windows = await CaptureWindowsAsync(dir, "promote", volume, reference, cancellationToken)
            .ConfigureAwait(false);
        await ListenRemainderAsync(DefaultListenOnB, cancellationToken).ConfigureAwait(false);
        _playback.Pause();

        var observation = await RequestObservationAsync(
                attemptId,
                "Natural gapless promotion of B finished (verified TrackTransitioned). How did that playback sound?",
                cancellationToken)
            .ConfigureAwait(false);

        _playback.Stop();

        var pcmSummary = AggregatePcmSummary(windows);
        return new ScenarioResult(
            AttemptId: attemptId,
            Scenario: "natural-promote",
            TrackTransitionedFired: true,
            TransitionId: args.TransitionId,
            SourcePath: player.SourcePath,
            DumpDirectory: dir,
            Observation: observation,
            Windows: windows,
            PcmSummary: pcmSummary,
            UnderrunCount: windows.Sum(w => w.UnderrunCount),
            ListeningVolume: volume,
            PathVerified: true);
    }

    private async Task<ScenarioResult> RunFreshLoadScenarioAsync(
        string trackB,
        string root,
        float volume,
        ListeningWavReference.Result? reference,
        CancellationToken cancellationToken)
    {
        var attemptId = "load-" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(root, "fresh-load");
        Directory.CreateDirectory(dir);

        Report("load.prepare", "Stopping prior session; fresh Load of B (gapless off for this scenario).");
        EnsureVolume(volume);
        _playback.Stop();
        _playback.SetGaplessEnabled(false);
        _playback.ClearNext();

        var transitionSeen = 0;
        var player = RequirePlayer();
        void OnTransitioned(object? s, TrackTransitionedEventArgs e) => Interlocked.Exchange(ref transitionSeen, 1);
        player.TrackTransitioned += OnTransitioned;

        try
        {
            await _playback.LoadAsync(trackB, cancellationToken).ConfigureAwait(false);
            Report("load.play", "Playing B via fresh Load.");
            await _playback.PlayAsync(cancellationToken).ConfigureAwait(false);

            var windows = await CaptureWindowsAsync(dir, "manual", volume, reference, cancellationToken)
                .ConfigureAwait(false);
            await ListenRemainderAsync(DefaultListenOnB, cancellationToken).ConfigureAwait(false);
            _playback.Pause();

            var fired = Volatile.Read(ref transitionSeen) != 0;
            if (!ListeningPathRules.IsVerifiedFreshLoad(fired, trackB, player.SourcePath))
            {
                throw new InvalidOperationException(
                    fired
                        ? "Fresh Load unexpectedly raised TrackTransitioned."
                        : $"Fresh Load source mismatch (source={player.SourcePath}).");
            }

            var observation = await RequestObservationAsync(
                    attemptId,
                    "Fresh Load of B finished (no TrackTransitioned). How did that playback sound?",
                    cancellationToken)
                .ConfigureAwait(false);

            _playback.Stop();

            var pcmSummary = AggregatePcmSummary(windows);
            return new ScenarioResult(
                AttemptId: attemptId,
                Scenario: "fresh-load",
                TrackTransitionedFired: false,
                TransitionId: 0,
                SourcePath: player.SourcePath,
                DumpDirectory: dir,
                Observation: observation,
                Windows: windows,
                PcmSummary: pcmSummary,
                UnderrunCount: windows.Sum(w => w.UnderrunCount),
                ListeningVolume: volume,
                PathVerified: true);
        }
        finally
        {
            player.TrackTransitioned -= OnTransitioned;
        }
    }

    private async Task<List<WindowResult>> CaptureWindowsAsync(
        string dir,
        string filePrefix,
        float volume,
        ListeningWavReference.Result? reference,
        CancellationToken cancellationToken)
    {
        var player = RequirePlayer();
        var results = new List<WindowResult>();
        foreach (var start in DefaultCaptureStarts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report("capture.window", $"Capturing {filePrefix} @ {start.TotalMilliseconds:F0} ms.");
            var captured = await ManagedOutputPositionCapture.CaptureWindowAtPositionAsync(
                    player,
                    () => _playback.LastWasapiOutput,
                    start,
                    CaptureWindow,
                    timeout: TimeSpan.FromSeconds(start.TotalSeconds + CaptureWindow.TotalSeconds + 45),
                    phase => Report("capture." + phase, phase),
                    cancellationToken)
                .ConfigureAwait(false);

            var name = $"{filePrefix}-w{start.TotalMilliseconds:F0}";
            var floatPath = Path.Combine(dir, name + ".f32");
            await File.WriteAllBytesAsync(floatPath, FloatBytes(captured.Samples), cancellationToken)
                .ConfigureAwait(false);

            ListeningPcmIntegrity.Report? pcm = null;
            if (reference is not null
                && captured.Format.SampleRate == reference.SampleRate
                && captured.Format.ChannelCount == reference.Channels)
            {
                pcm = ListeningPcmIntegrity.CompareCaptureToReference(
                    captured.Samples,
                    reference.Samples,
                    captured.Format.SampleRate,
                    captured.Format.ChannelCount,
                    captured.ArmedAtPosition,
                    volume);
            }
            else if (reference is not null)
            {
                pcm = new ListeningPcmIntegrity.Report
                {
                    Suitable = false,
                    Reason = "Session format differs from WAV reference — sample-index compare skipped.",
                    Summary = "inconclusive-format",
                    ListeningVolume = volume,
                    ArmedAtMs = captured.ArmedAtPosition.TotalMilliseconds,
                };
            }

            var window = new WindowResult(
                Label: name,
                RequestedStartMs: start.TotalMilliseconds,
                ArmedAtMs: captured.ArmedAtPosition.TotalMilliseconds,
                SampleCount: captured.Samples.Length,
                SampleRate: captured.Format.SampleRate,
                ChannelCount: captured.Format.ChannelCount,
                UnderrunCount: captured.UnderrunCount,
                OutputMode: captured.OutputMode,
                FloatFile: Path.GetFileName(floatPath),
                Pcm: pcm);

            await File.WriteAllTextAsync(
                    Path.Combine(dir, name + ".json"),
                    JsonSerializer.Serialize(window, JsonOptions),
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);

            results.Add(window);
        }

        return results;
    }

    private async Task ListenRemainderAsync(TimeSpan listenOnB, CancellationToken cancellationToken)
    {
        // Captures already consumed part of the listen window; keep playing so the user can hear static.
        var player = RequirePlayer();
        if (player.State is PlaybackState.Paused)
        {
            player.Play();
        }

        var remaining = listenOnB - player.Position;
        if (remaining < TimeSpan.FromSeconds(2))
        {
            remaining = TimeSpan.FromSeconds(2);
        }

        Report("listening", $"Continue listening for ~{remaining.TotalSeconds:F0}s…");
        try
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private async Task<ListeningObservation> RequestObservationAsync(
        string attemptId,
        string prompt,
        CancellationToken cancellationToken)
    {
        Report("awaitObservation", prompt, ("attemptId", attemptId));
        PhaseChanged?.Invoke(
            UiEventNames.ListeningAbAwaitObservation,
            new { attemptId, prompt, phase = "awaitObservation" });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            return await _observations.WaitAsync(attemptId, prompt, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            PhaseChanged?.Invoke(
                UiEventNames.ListeningAbObservationReceived,
                new { attemptId, phase = "observationReceived" });
        }
    }

    private void EnsureVolume(float volume)
    {
        // Keep the user's comfortable TestPlayer volume; re-apply so both scenarios match.
        _playback.SetVolume(volume);
    }

    private AudioPlayer RequirePlayer() =>
        _playback.Player ?? throw new InvalidOperationException("Player is not created.");

    private static async Task WaitReadyAsync(AudioPlayer player, string expectedB, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            if (player.NextTrackState == NextTrackState.Ready
                && PathsEqual(player.NextTrackPath, expectedB))
            {
                return;
            }

            if (player.NextTrackState == NextTrackState.Failed)
            {
                throw new InvalidOperationException("NextTrackState=Failed; cannot verify promote.");
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"NextTrackState not Ready for B (state={player.NextTrackState}).");
    }

    private static TimeSpan TransitionWaitTimeout(AudioPlayer player)
    {
        var remaining = player.HasKnownDuration ? player.Duration - player.Position : TimeSpan.FromMinutes(2);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var wait = remaining + TimeSpan.FromSeconds(45);
        return wait > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : wait;
    }

    private static string AggregatePcmSummary(IReadOnlyList<WindowResult> windows)
    {
        if (windows.Count == 0)
        {
            return "no-capture";
        }

        if (windows.Any(w => w.Pcm?.SampleIdentity == true))
        {
            if (windows.All(w => w.Pcm is null || w.Pcm.SampleIdentity || !w.Pcm.Suitable
                || w.Pcm.Summary.Contains("silence", StringComparison.OrdinalIgnoreCase)
                || w.Pcm.Summary.Contains("inconclusive", StringComparison.OrdinalIgnoreCase)))
            {
                return "PCM exact";
            }
        }

        if (windows.Any(w => w.Pcm is { Suitable: true, AlignmentInconclusive: false, SampleIdentity: false }))
        {
            return "PCM differs";
        }

        return "PCM inconclusive";
    }

    private static ListeningClassification ParseClassification(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Equals("Clean", StringComparison.OrdinalIgnoreCase))
        {
            return ListeningClassification.Clean;
        }

        if (raw.Equals("Static", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Distorted", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("static", StringComparison.OrdinalIgnoreCase))
        {
            return ListeningClassification.Static;
        }

        return ListeningClassification.Unsure;
    }

    private static bool PathsEqual(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static byte[] FloatBytes(float[] samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(float))];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private void Report(string phase, string message, params (string Key, string Value)[] data)
    {
        var pairs = new List<(string Key, string Value)> { ("phase", phase) };
        pairs.AddRange(data);
        _store.Add("diagnostics", DiagnosticLevel.Info, "listeningAb." + phase, message, pairs.ToArray());
        try
        {
            PhaseChanged?.Invoke(
                UiEventNames.ListeningAbPhase,
                new { phase, message, data = pairs.ToDictionary(static p => p.Key, static p => p.Value) });
        }
        catch
        {
            // UI push must never break the diagnostic.
        }
    }

    private sealed record WindowResult(
        string Label,
        double RequestedStartMs,
        double ArmedAtMs,
        int SampleCount,
        int SampleRate,
        int ChannelCount,
        long UnderrunCount,
        string OutputMode,
        string FloatFile,
        ListeningPcmIntegrity.Report? Pcm);

    private sealed record ScenarioResult(
        string AttemptId,
        string Scenario,
        bool TrackTransitionedFired,
        int TransitionId,
        string? SourcePath,
        string DumpDirectory,
        ListeningObservation Observation,
        List<WindowResult> Windows,
        string PcmSummary,
        long UnderrunCount,
        float ListeningVolume,
        bool PathVerified);
}
