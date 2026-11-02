namespace Sonora.TestPlayer.Models;

/// <summary>Manual verification outcome for a scenario. Scenarios are never marked passed automatically.</summary>
public enum ScenarioStatus
{
    /// <summary>Not yet attempted.</summary>
    NotRun = 0,

    /// <summary>The tester judged the observed behavior correct.</summary>
    Pass = 1,

    /// <summary>The tester judged the observed behavior incorrect.</summary>
    Fail = 2,

    /// <summary>The tester could not reach a verdict (hardware unavailable, unclear result).</summary>
    Inconclusive = 3,
}

/// <summary>Grouping for the scenario catalog.</summary>
public enum ScenarioCategory
{
    /// <summary>Transport and queue behavior.</summary>
    Playback = 0,

    /// <summary>Gapless transitions and preload.</summary>
    Gapless = 1,

    /// <summary>Seeking accuracy and bounds.</summary>
    Seeking = 2,

    /// <summary>Output devices and exclusive/shared modes.</summary>
    Output = 3,

    /// <summary>Equalizer, preamp, limiter.</summary>
    Dsp = 4,

    /// <summary>HTTP/HTTPS streaming.</summary>
    Streaming = 5,

    /// <summary>Lifecycle, disposal, and rapid-command robustness.</summary>
    Robustness = 6,

    /// <summary>Long-running stability.</summary>
    Soak = 7,
}

/// <summary>Static description of a manual test scenario.</summary>
/// <param name="Id">Stable identifier, for example <c>gapless.two-track</c>.</param>
/// <param name="Category">Catalog category.</param>
/// <param name="Title">Short title.</param>
/// <param name="Steps">Ordered manual steps.</param>
/// <param name="Expected">What a correct implementation should do.</param>
/// <param name="RequiresHardware">Whether a real audio device (or specific hardware) is needed.</param>
public sealed record ScenarioDefinition(
    string Id,
    ScenarioCategory Category,
    string Title,
    IReadOnlyList<string> Steps,
    string Expected,
    bool RequiresHardware = true);

/// <summary>Recorded outcome of a scenario.</summary>
public sealed record ScenarioResult
{
    /// <summary>Scenario id from <see cref="ScenarioDefinition.Id"/>.</summary>
    public required string ScenarioId { get; init; }

    /// <summary>Verdict. Defaults to <see cref="ScenarioStatus.NotRun"/>.</summary>
    public ScenarioStatus Status { get; init; } = ScenarioStatus.NotRun;

    /// <summary>Tester notes.</summary>
    public string? Notes { get; init; }

    /// <summary>When the verdict was recorded, or <see langword="null"/> when not run.</summary>
    public DateTimeOffset? RecordedAtUtc { get; init; }
}

/// <summary>Summary of a soak session tracked by the scenario controller.</summary>
public sealed record SoakSessionDto
{
    /// <summary>Whether a soak session is currently running.</summary>
    public bool Active { get; init; }

    /// <summary>Session start (UTC), or <see langword="null"/> when none has started.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>Session end (UTC), or <see langword="null"/> while running / not started.</summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>Elapsed session time in seconds.</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>Error events recorded in the diagnostic store during the session.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Underrun events recorded in the diagnostic store during the session.</summary>
    public int UnderrunCount { get; init; }

    /// <summary>Process working set (bytes) at session start — process-level, not Sonora-attributable.</summary>
    public long? StartWorkingSetBytes { get; init; }

    /// <summary>Latest observed process working set (bytes).</summary>
    public long? LatestWorkingSetBytes { get; init; }

    /// <summary>Peak observed process working set (bytes) during the session.</summary>
    public long? PeakWorkingSetBytes { get; init; }

    /// <summary>Tester notes (timestamped lines).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Scenario results and soak summary, embedded in diagnostic exports.</summary>
public sealed record ScenarioReportDto
{
    /// <summary>Results for every catalog scenario (including <see cref="ScenarioStatus.NotRun"/>).</summary>
    public IReadOnlyList<ScenarioResult> Results { get; init; } = [];

    /// <summary>Soak session summary.</summary>
    public SoakSessionDto Soak { get; init; } = new();
}
