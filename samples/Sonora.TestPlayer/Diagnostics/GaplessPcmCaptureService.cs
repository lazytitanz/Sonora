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
/// Developer-only: position-gated managed→WASAPI PCM capture for natural promote vs manual Load.
/// All waits and file writes run off the audio callback.
/// </summary>
public sealed class GaplessPcmCaptureService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PlaybackController _playback;
    private readonly DiagnosticEventStore _store;
    private readonly object _gate = new();
    private string? _lastPromotePath;
    private string? _lastManualPath;
    private int _busy;

    public GaplessPcmCaptureService(PlaybackController playback, DiagnosticEventStore store)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Optional UI push for live phase updates (<see cref="UiEventNames.GaplessPcmPhase"/>).
    /// Invoked off the audio callback.
    /// </summary>
    public Action<string, object?>? PhaseChanged { get; set; }

    public string? LastPromoteCaptureDirectory
    {
        get { lock (_gate) return _lastPromotePath; }
    }

    public string? LastManualCaptureDirectory
    {
        get { lock (_gate) return _lastManualPath; }
    }

    /// <summary>
    /// Waits for the next Sonora <c>TrackTransitioned</c> (real gapless promote), then captures when
    /// Position reaches <paramref name="positionStartMs"/> on B.
    /// </summary>
    public async Task<object> CaptureAfterNaturalPromoteAsync(
        string outputDirectory,
        double positionStartMs = 3000,
        double windowMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            throw new InvalidOperationException("A gapless PCM capture is already in progress.");
        }

        try
        {
            var player = RequirePlayer();
            var positionStart = TimeSpan.FromMilliseconds(positionStartMs);
            var window = TimeSpan.FromMilliseconds(windowMs);
            var dir = PrepareScenarioDir(outputDirectory, "promote");

            Report(
                "checkingPrerequisites",
                "Checking gapless preload (NextTrackState must be Ready for a real promote).");

            if (!_playback.GaplessEnabled)
            {
                throw new InvalidOperationException(
                    "Gapless is disabled. Enable gapless so A→B uses TrackTransitioned, not a fresh Load.");
            }

            if (!player.HasSource)
            {
                throw new InvalidOperationException(
                    "No track is loaded. Load/play A with B next in the queue, then start this capture before A ends.");
            }

            // Ensure the engine has a Ready next; otherwise A EOS uses PlaybackCompleted + Load.
            await _playback.PreloadNextAsync(cancellationToken).ConfigureAwait(false);
            await WaitForNextReadyAsync(player, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            var nextPath = player.NextTrackPath;
            var nextState = player.NextTrackState;
            Report(
                "waitingForTransition",
                $"Subscribed on player; waiting for TrackTransitioned (next={Path.GetFileName(nextPath) ?? "?"} state={nextState}). "
                + "If A ends via ordinary Load, this capture fails.",
                ("nextState", nextState.ToString()),
                ("nextPath", nextPath ?? string.Empty),
                ("dir", dir));

            var transitioned = new TaskCompletionSource<TrackTransitionedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completedWithoutPromote = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnTransitioned(object? sender, TrackTransitionedEventArgs e) =>
                transitioned.TrySetResult(e);

            void OnCompleted(object? sender, EventArgs e)
            {
                // If completion wins the race, Sonora did not gapless-promote into B.
                completedWithoutPromote.TrySetResult(
                    $"PlaybackCompleted fired while waiting for TrackTransitioned "
                    + $"(source={Path.GetFileName(player.SourcePath) ?? "?"}, nextState={player.NextTrackState}). "
                    + "A→B used TestPlayer's Load-after-completion path, not Sonora gapless promote.");
            }

            player.TrackTransitioned += OnTransitioned;
            player.PlaybackCompleted += OnCompleted;
            try
            {
                var waitTimeout = ComputeTransitionWaitTimeout(player);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(waitTimeout);

                var transitionTask = transitioned.Task;
                var completedTask = completedWithoutPromote.Task;
                var winner = await Task.WhenAny(transitionTask, completedTask)
                    .WaitAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                if (ReferenceEquals(winner, completedTask))
                {
                    throw new InvalidOperationException(await completedTask.ConfigureAwait(false));
                }

                var args = await transitionTask.ConfigureAwait(false);
                Report(
                    "transitionObserved",
                    $"TrackTransitioned id={args.TransitionId} → {Path.GetFileName(args.NextPath) ?? "?"}.",
                    ("transitionId", args.TransitionId.ToString(CultureInfo.InvariantCulture)),
                    ("nextPath", args.NextPath ?? string.Empty),
                    ("previousPath", args.PreviousPath ?? string.Empty));

                // Drop completion handler so B's eventual EOS does not confuse us.
                player.PlaybackCompleted -= OnCompleted;

                Report(
                    "waitingForPosition",
                    $"Waiting for Position>={positionStart.TotalMilliseconds:F0} ms on promoted B.");

                var result = await ManagedOutputPositionCapture.CaptureWindowAtPositionAsync(
                        player,
                        () => _playback.LastWasapiOutput,
                        positionStart,
                        window,
                        timeout: TimeSpan.FromSeconds(positionStart.TotalSeconds + window.TotalSeconds + 45),
                        phase => Report(phase, PhaseMessage(phase, positionStart, window)),
                        cancellationToken)
                    .ConfigureAwait(false);

                Report(
                    "captureCompleted",
                    $"Promote capture saved ({result.Samples.Length} samples).",
                    ("dir", dir),
                    ("samples", result.Samples.Length.ToString(CultureInfo.InvariantCulture)));

                await SaveCaptureAsync(dir, "promote", result, args.TransitionId, cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    _lastPromotePath = dir;
                }

                return Summarize(dir, "promote", result, args.TransitionId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Report("captureFailed", "Timed out waiting for TrackTransitioned.");
                throw new TimeoutException(
                    "Timed out waiting for TrackTransitioned. "
                    + "Confirm NextTrackState was Ready before A ended (gapless preload). "
                    + "If B still started, the queue likely advanced via PlaybackCompleted + Load — not a natural promote.");
            }
            catch (Exception ex)
            {
                Report("captureFailed", ex.Message);
                throw;
            }
            finally
            {
                player.TrackTransitioned -= OnTransitioned;
                player.PlaybackCompleted -= OnCompleted;
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>
    /// Captures when Position reaches <paramref name="positionStartMs"/> on the currently playing track
    /// (manual Load path).
    /// </summary>
    public async Task<object> CaptureManualLoadAsync(
        string outputDirectory,
        double positionStartMs = 3000,
        double windowMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            throw new InvalidOperationException("A gapless PCM capture is already in progress.");
        }

        try
        {
            var player = RequirePlayer();
            var positionStart = TimeSpan.FromMilliseconds(positionStartMs);
            var window = TimeSpan.FromMilliseconds(windowMs);
            var dir = PrepareScenarioDir(outputDirectory, "manual");

            if (player.State is not (PlaybackState.Playing or PlaybackState.Buffering))
            {
                throw new InvalidOperationException("Start playback (manual Load of B) before capturing.");
            }

            Report(
                "waitingForPosition",
                $"Manual path: waiting for Position>={positionStart.TotalMilliseconds:F0} ms.",
                ("dir", dir));

            var result = await ManagedOutputPositionCapture.CaptureWindowAtPositionAsync(
                    player,
                    () => _playback.LastWasapiOutput,
                    positionStart,
                    window,
                    timeout: TimeSpan.FromSeconds(positionStart.TotalSeconds + window.TotalSeconds + 45),
                    phase => Report(phase, PhaseMessage(phase, positionStart, window)),
                    cancellationToken)
                .ConfigureAwait(false);

            Report(
                "captureCompleted",
                $"Manual capture saved ({result.Samples.Length} samples).",
                ("dir", dir));

            await SaveCaptureAsync(dir, "manual", result, transitionId: result.TransitionId, cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _lastManualPath = dir;
            }

            return Summarize(dir, "manual", result, result.TransitionId);
        }
        catch (Exception ex)
        {
            Report("captureFailed", ex.Message);
            throw;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Compares the last promote and manual float dumps with NCC alignment.</summary>
    public object CompareLastCaptures(int maxLagMs = 100)
    {
        string? promoteDir;
        string? manualDir;
        lock (_gate)
        {
            promoteDir = _lastPromotePath;
            manualDir = _lastManualPath;
        }

        if (string.IsNullOrWhiteSpace(promoteDir) || string.IsNullOrWhiteSpace(manualDir))
        {
            throw new InvalidOperationException(
                "Need both a promote and a manual capture. Run captureAfterPromote and captureManual first.");
        }

        return GaplessPcmCompare.CompareDirectories(promoteDir, manualDir, maxLagMs);
    }

    private async Task WaitForNextReadyAsync(
        AudioPlayer player,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (player.NextTrackState == NextTrackState.Ready
                && !string.IsNullOrEmpty(player.NextTrackPath))
            {
                return;
            }

            if (player.NextTrackState == NextTrackState.Failed)
            {
                throw new InvalidOperationException(
                    "NextTrackState is Failed. Fix preload before capturing a natural promote "
                    + "(format conversion / decode error). A→B would otherwise Load after completion.");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"NextTrackState did not become Ready within {timeout.TotalSeconds:F0}s "
            + $"(state={player.NextTrackState}, path={Path.GetFileName(player.NextTrackPath) ?? "none"}). "
            + "Without a Ready next track, Sonora will not raise TrackTransitioned; TestPlayer will Load B on PlaybackCompleted.");
    }

    private static TimeSpan ComputeTransitionWaitTimeout(AudioPlayer player)
    {
        // Remain of A + margin; clamp so we never hang for an unbounded playlist.
        var remaining = player.HasKnownDuration
            ? player.Duration - player.Position
            : TimeSpan.FromMinutes(2);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var wait = remaining + TimeSpan.FromSeconds(45);
        if (wait < TimeSpan.FromSeconds(60))
        {
            wait = TimeSpan.FromSeconds(60);
        }

        if (wait > TimeSpan.FromMinutes(4))
        {
            wait = TimeSpan.FromMinutes(4);
        }

        return wait;
    }

    private static string PhaseMessage(string phase, TimeSpan positionStart, TimeSpan window) =>
        phase switch
        {
            "waitingForPosition" => $"Waiting for Position>={positionStart.TotalMilliseconds:F0} ms.",
            "captureArmed" => $"Capture armed for {window.TotalMilliseconds:F0} ms window.",
            "captureCompleted" => "Managed-boundary capture window filled.",
            _ => phase,
        };

    private void Report(string phase, string message, params (string Key, string Value)[] data)
    {
        var pairs = new (string Key, string Value)[data.Length + 1];
        pairs[0] = ("phase", phase);
        for (var i = 0; i < data.Length; i++)
        {
            pairs[i + 1] = data[i];
        }

        _store.Add("diagnostics", DiagnosticLevel.Info, "gaplessPcm." + phase, message, pairs);
        try
        {
            PhaseChanged?.Invoke(
                UiEventNames.GaplessPcmPhase,
                new { phase, message, data = pairs.ToDictionary(static p => p.Key, static p => p.Value) });
        }
        catch
        {
            // UI push must never break capture.
        }
    }

    private AudioPlayer RequirePlayer() =>
        _playback.Player ?? throw new InvalidOperationException("Player is not created.");

    private static string PrepareScenarioDir(string outputDirectory, string scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(outputDirectory, $"gapless-pcm-{scenario}-{stamp}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task SaveCaptureAsync(
        string directory,
        string scenario,
        ManagedOutputCaptureResult result,
        int transitionId,
        CancellationToken cancellationToken)
    {
        var floatPath = Path.Combine(directory, $"{scenario}.f32");
        var metaPath = Path.Combine(directory, $"{scenario}.json");

        await File.WriteAllBytesAsync(
                floatPath,
                FloatBytes(result.Samples),
                cancellationToken)
            .ConfigureAwait(false);

        var meta = new Dictionary<string, object?>
        {
            ["scenario"] = scenario,
            ["sourcePath"] = result.SourcePath,
            ["sampleRate"] = result.Format.SampleRate,
            ["channelCount"] = result.Format.ChannelCount,
            ["sampleCount"] = result.Samples.Length,
            ["requestedStartMs"] = result.RequestedStart.TotalMilliseconds,
            ["armedAtPositionMs"] = result.ArmedAtPosition.TotalMilliseconds,
            ["windowMs"] = result.Window.TotalMilliseconds,
            ["transitionId"] = transitionId,
            ["lastSilenceReason"] = result.LastSilenceReason,
            ["underrunCount"] = result.UnderrunCount,
            ["outputMode"] = result.OutputMode,
            ["endpointId"] = result.EndpointId,
            ["floatFile"] = Path.GetFileName(floatPath),
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await File.WriteAllTextAsync(
                metaPath,
                JsonSerializer.Serialize(meta, JsonOptions),
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] FloatBytes(float[] samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(float))];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static object Summarize(
        string directory,
        string scenario,
        ManagedOutputCaptureResult result,
        int transitionId) =>
        new
        {
            directory,
            scenario,
            sampleCount = result.Samples.Length,
            sampleRate = result.Format.SampleRate,
            channelCount = result.Format.ChannelCount,
            armedAtPositionMs = result.ArmedAtPosition.TotalMilliseconds,
            requestedStartMs = result.RequestedStart.TotalMilliseconds,
            windowMs = result.Window.TotalMilliseconds,
            transitionId,
            sourcePath = result.SourcePath is null ? null : Path.GetFileName(result.SourcePath),
            lastSilenceReason = result.LastSilenceReason,
            underrunCount = result.UnderrunCount,
            outputMode = result.OutputMode,
        };
}
