using System.Diagnostics;
using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Controllers;

/// <summary>
/// Catalog of manual verification scenarios, tester-recorded results, and soak-session tracking.
/// </summary>
/// <remarks>
/// <para><b>Never auto-pass.</b> Nothing in this class decides a scenario passed. Results change only when a tester
/// calls <see cref="Record"/>; every scenario starts and stays <see cref="ScenarioStatus.NotRun"/> until then. Soak
/// tracking only <em>collects</em> numbers (error/underrun counts from the diagnostic store and process-level
/// memory); judging them is left to the tester.</para>
/// <para><b>Threading.</b> All members are thread-safe.</para>
/// </remarks>
public sealed class ScenarioController
{
    private static readonly IReadOnlyList<ScenarioDefinition> CatalogEntries =
    [
        // Playback
        new("playback.basic", ScenarioCategory.Playback, "Basic play / pause / stop",
            ["Queue a local file and press Play.", "Pause, then Play again.", "Stop."],
            "Pause keeps position; Stop resets to 0; no audio glitches or errors in diagnostics."),
        new("playback.rapid-commands", ScenarioCategory.Playback, "Rapid command sequence",
            ["Press Play/Pause/Stop/Next in quick succession 20+ times.", "Finish with Play."],
            "The player ends in a consistent state, nothing hangs, and only one playback session is audible."),
        new("playback.completion-single", ScenarioCategory.Playback, "Natural completion of the last track",
            ["Queue one short track and let it finish."],
            "Exactly one 'completed' event with reason NoMoreTracks; state is Stopped."),
        new("playback.explicit-stop-no-advance", ScenarioCategory.Playback, "Stop never advances the queue",
            ["Queue two tracks, play the first, press Stop near its end."],
            "No automatic advance to the second track; no completion-advance counted."),
        new("queue.advance-nongapless", ScenarioCategory.Playback, "Queue advance without gapless",
            ["Disable gapless.", "Queue two tracks and let the first finish."],
            "The second track loads and plays automatically via PlaybackCompleted; one advance only."),
        new("queue.edit-while-playing", ScenarioCategory.Playback, "Edit queue while playing",
            ["Play track 1 of 3.", "Remove and reorder upcoming tracks."],
            "Playback continues; the next track follows the edited queue order."),

        // Gapless
        new("gapless.two-track", ScenarioCategory.Gapless, "Two-track gapless transition",
            ["Enable gapless.", "Queue two tracks that join seamlessly (e.g. a continuous album).", "Listen across the boundary."],
            "No audible gap or click; one TrackTransitioned; queue index advances once; no duplicate completion advance."),
        new("gapless.pcm-boundary-diag", ScenarioCategory.Gapless,
            "Managed-boundary PCM: natural promote vs manual Load (developer)",
            [
                "Use a file that previously distorted on natural promote (e.g. test-audio-wav-16bit-44khz.wav) as track B.",
                "Queue a short A then B; enable gapless; bypass DSP; open Diagnostics → Gapless PCM.",
                "Set an output folder; click Capture after promote; Play and listen — note whether B is distorted.",
                "Stop; Load only B; Play; Capture manual at the same Position (~3 s).",
                "Click Compare last; record listen result + compare summary in scenario notes.",
            ],
            "Promote vs Load mid-B PCM compared at the managed→WASAPI boundary; listen class recorded by the tester (never auto-pass)."),
        new("gapless.format-change", ScenarioCategory.Gapless, "Gapless across sample-rate change",
            ["Queue a 44.1 kHz then a 48 kHz track."],
            "Transition succeeds (converted to the session format) without error."),
        new("gapless.load-during-preload", ScenarioCategory.Gapless, "Load while next track is preloading",
            ["Play track 1, immediately load a different track."],
            "The stale preload is discarded; no stale transition advances the queue."),

        // Seeking
        new("seek.accuracy", ScenarioCategory.Seeking, "Seek accuracy report",
            ["Seek to several positions with 'Seek with result'.", "Compare requested vs actual and the accuracy label."],
            "Accuracy is reported honestly (Exact / Approximate / Unknown); actual position is within expectations for the format."),
        new("seek.bounds", ScenarioCategory.Seeking, "Seek boundary values",
            ["Seek to 0, to duration, and beyond duration.", "Attempt a negative position."],
            "Positions clamp or are rejected predictably; negative values are rejected; no crash."),

        // Output
        new("output.device-switch", ScenarioCategory.Output, "Switch output device during playback",
            ["Play, then choose another device."],
            "Playback resumes on the new device with position preserved; device status returns to Available."),
        new("output.device-unplug", ScenarioCategory.Output, "Unplug the active device",
            ["Select a USB/Bluetooth device and play.", "Disconnect it."],
            "Status moves to PreferredUnavailable/Unavailable and recovers when the device returns per recovery intent."),
        new("output.exclusive", ScenarioCategory.Output, "Exclusive mode",
            ["Request Exclusive with fallback None.", "Play a track."],
            "Effective mode is Exclusive, or a classified failure is reported (no silent fallback)."),
        new("output.exclusive-fallback", ScenarioCategory.Output, "Exclusive with shared fallback",
            ["Hold the device in another exclusive app.", "Request Exclusive with fallback Shared."],
            "Effective mode is Shared and the fallback reason (e.g. DeviceBusy) is shown."),

        // DSP
        new("dsp.eq-audible", ScenarioCategory.Dsp, "Equalizer audibly changes tone",
            ["Play music.", "Apply Bass Boost, then Flat."],
            "Audible low-end change with no clicks when switching presets."),
        new("dsp.limiter-ceiling", ScenarioCategory.Dsp, "Limiter prevents clipping",
            ["Raise preamp to +12 dB on loud material.", "Toggle the limiter."],
            "With the limiter enabled the output does not clip at the configured ceiling (sample-peak only; not true-peak)."),
        new("dsp.bypass", ScenarioCategory.Dsp, "Rack bypass",
            ["Set an extreme EQ curve.", "Toggle rack bypass."],
            "Bypass restores the unprocessed sound without clicks."),

        // Streaming
        new("stream.basic", ScenarioCategory.Streaming, "Stream a public HTTPS audio file",
            ["Load a public audio URL with default (restricted) safety mode."],
            "Network status moves Connecting → Buffering → Ready; audio plays; position advances.",
            RequiresHardware: true),
        new("stream.blocked-local", ScenarioCategory.Streaming, "Local URL blocked in restricted mode",
            ["Try http://127.0.0.1/ and http://192.168.1.10/ with the developer override off."],
            "Both are rejected with a reason; with the override on they are attempted.",
            RequiresHardware: false),
        new("stream.network-loss", ScenarioCategory.Streaming, "Network interruption and recovery",
            ["Stream a long file.", "Disable the network briefly, then re-enable."],
            "Status shows Buffering/Reconnecting then Ready, or Failed after retries are exhausted; no hang."),

        // Robustness
        new("robustness.shutdown-during-play", ScenarioCategory.Robustness, "Close the app while playing",
            ["Start playback, then close the window."],
            "The process exits cleanly with no hung audio device or lingering process.",
            RequiresHardware: false),
        new("robustness.corrupt-file", ScenarioCategory.Robustness, "Corrupt / unsupported file",
            ["Queue a truncated or non-audio file renamed to .mp3 and play it."],
            "A clear error is reported (unsupported vs invalid); the player stays usable.",
            RequiresHardware: false),

        // Soak
        new("soak.one-hour", ScenarioCategory.Soak, "One-hour soak",
            ["Start a soak session.", "Loop a multi-track queue with gapless on for one hour.", "Stop the session and review counts."],
            "No growth trend in process memory/handles beyond noise; zero unexpected errors; underruns understood."),
    ];

    private readonly DiagnosticEventStore _store;
    private readonly ProcessResourceSampler _sampler;
    private readonly object _sync = new();
    private readonly Dictionary<string, ScenarioResult> _results = new(StringComparer.Ordinal);

    private bool _soakActive;
    private DateTimeOffset? _soakStartedUtc;
    private DateTimeOffset? _soakEndedUtc;
    private long _soakStartTimestamp;
    private long _soakEndTimestamp;
    private long _soakStartErrors;
    private long _soakStartUnderruns;
    private long _soakEndErrors;
    private long _soakEndUnderruns;
    private long? _soakStartWorkingSet;
    private long? _soakLatestWorkingSet;
    private long? _soakPeakWorkingSet;
    private readonly List<string> _soakNotes = [];

    /// <summary>Creates the controller.</summary>
    public ScenarioController(DiagnosticEventStore store, ProcessResourceSampler sampler)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
    }

    /// <summary>All scenario definitions, grouped by category in declaration order.</summary>
    public static IReadOnlyList<ScenarioDefinition> Catalog => CatalogEntries;

    /// <summary>Returns one result per catalog scenario (<see cref="ScenarioStatus.NotRun"/> when unrecorded).</summary>
    public IReadOnlyList<ScenarioResult> GetResults()
    {
        lock (_sync)
        {
            return CatalogEntries
                .Select(d => _results.TryGetValue(d.Id, out var r) ? r : new ScenarioResult { ScenarioId = d.Id })
                .ToArray();
        }
    }

    /// <summary>Records a tester verdict for a scenario (use <see cref="ScenarioStatus.NotRun"/> to clear it).</summary>
    /// <exception cref="ArgumentException">The scenario id is not in the catalog.</exception>
    public ScenarioResult Record(string scenarioId, ScenarioStatus status, string? notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown scenario status.");
        }

        if (!CatalogEntries.Any(d => d.Id == scenarioId))
        {
            throw new ArgumentException($"Unknown scenario '{scenarioId}'.", nameof(scenarioId));
        }

        var result = new ScenarioResult
        {
            ScenarioId = scenarioId,
            Status = status,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            RecordedAtUtc = status == ScenarioStatus.NotRun ? null : DateTimeOffset.UtcNow,
        };

        lock (_sync)
        {
            _results[scenarioId] = result;
        }

        _store.Add("scenario", DiagnosticLevel.Info, "scenario.recorded", $"{scenarioId}: {status}", ("scenarioId", scenarioId), ("status", status.ToString()));
        return result;
    }

    /// <summary>Clears every recorded result.</summary>
    public void ResetResults()
    {
        lock (_sync)
        {
            _results.Clear();
        }

        _store.Add("scenario", DiagnosticLevel.Info, "scenario.reset", "All scenario results cleared.");
    }

    // ---- Soak ----

    /// <summary>Starts a soak session, snapshotting cumulative error/underrun counts and process memory.</summary>
    /// <exception cref="InvalidOperationException">A session is already active.</exception>
    public SoakSessionDto StartSoak()
    {
        var sample = _sampler.Sample();
        lock (_sync)
        {
            if (_soakActive)
            {
                throw new InvalidOperationException("A soak session is already running.");
            }

            _soakActive = true;
            _soakStartedUtc = DateTimeOffset.UtcNow;
            _soakEndedUtc = null;
            _soakStartTimestamp = Stopwatch.GetTimestamp();
            _soakEndTimestamp = 0;
            _soakStartErrors = _store.TotalByLevel(DiagnosticLevel.Error);
            _soakStartUnderruns = _store.TotalByName(DiagnosticEventNames.Underrun);
            _soakStartWorkingSet = sample.WorkingSetBytes;
            _soakLatestWorkingSet = sample.WorkingSetBytes;
            _soakPeakWorkingSet = sample.WorkingSetBytes;
            _soakNotes.Clear();
        }

        _store.Add("scenario", DiagnosticLevel.Info, "soak.started", "Soak session started.");
        return GetSoakSession();
    }

    /// <summary>Stops the active soak session and freezes its counts.</summary>
    /// <exception cref="InvalidOperationException">No session is active.</exception>
    public SoakSessionDto StopSoak()
    {
        lock (_sync)
        {
            if (!_soakActive)
            {
                throw new InvalidOperationException("No soak session is running.");
            }

            _soakActive = false;
            _soakEndedUtc = DateTimeOffset.UtcNow;
            _soakEndTimestamp = Stopwatch.GetTimestamp();
            _soakEndErrors = _store.TotalByLevel(DiagnosticLevel.Error);
            _soakEndUnderruns = _store.TotalByName(DiagnosticEventNames.Underrun);
        }

        _store.Add("scenario", DiagnosticLevel.Info, "soak.stopped", "Soak session stopped.");
        return GetSoakSession();
    }

    /// <summary>Appends a timestamped tester note to the soak session.</summary>
    public SoakSessionDto AddSoakNote(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        lock (_sync)
        {
            var elapsed = _soakStartedUtc is null
                ? TimeSpan.Zero
                : Stopwatch.GetElapsedTime(_soakStartTimestamp, _soakActive ? Stopwatch.GetTimestamp() : _soakEndTimestamp);
            _soakNotes.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss}Z +{elapsed:hh\\:mm\\:ss}] {note.Trim()}");
        }

        return GetSoakSession();
    }

    /// <summary>Feeds a process resource sample into the soak peak/latest tracking. Call after each sampler tick.</summary>
    public void ObserveResources(ProcessResourceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_sync)
        {
            if (!_soakActive)
            {
                return;
            }

            _soakLatestWorkingSet = sample.WorkingSetBytes;
            _soakPeakWorkingSet = Math.Max(_soakPeakWorkingSet ?? 0, sample.WorkingSetBytes);
        }
    }

    /// <summary>Returns the current soak session summary.</summary>
    public SoakSessionDto GetSoakSession()
    {
        lock (_sync)
        {
            if (_soakStartedUtc is null)
            {
                return new SoakSessionDto();
            }

            var errors = _soakActive ? _store.TotalByLevel(DiagnosticLevel.Error) : _soakEndErrors;
            var underruns = _soakActive ? _store.TotalByName(DiagnosticEventNames.Underrun) : _soakEndUnderruns;
            var end = _soakActive ? Stopwatch.GetTimestamp() : _soakEndTimestamp;

            return new SoakSessionDto
            {
                Active = _soakActive,
                StartedAtUtc = _soakStartedUtc,
                EndedAtUtc = _soakEndedUtc,
                ElapsedSeconds = Stopwatch.GetElapsedTime(_soakStartTimestamp, end).TotalSeconds,
                ErrorCount = (int)Math.Min(int.MaxValue, errors - _soakStartErrors),
                UnderrunCount = (int)Math.Min(int.MaxValue, underruns - _soakStartUnderruns),
                StartWorkingSetBytes = _soakStartWorkingSet,
                LatestWorkingSetBytes = _soakLatestWorkingSet,
                PeakWorkingSetBytes = _soakPeakWorkingSet,
                Notes = _soakNotes.ToArray(),
            };
        }
    }

    /// <summary>Builds the scenario section for a diagnostic export.</summary>
    public ScenarioReportDto GetReport() => new()
    {
        Results = GetResults(),
        Soak = GetSoakSession(),
    };
}
