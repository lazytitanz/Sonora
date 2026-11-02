using System.Reflection;
using System.Runtime.InteropServices;
using Sonora.Audio;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Diagnostics;

/// <summary>Static facts about the host environment for a diagnostic report.</summary>
public sealed record EnvironmentInfoDto
{
    /// <summary>OS description.</summary>
    public string Os { get; init; } = string.Empty;

    /// <summary>.NET runtime description.</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>Process architecture.</summary>
    public string ProcessArchitecture { get; init; } = string.Empty;

    /// <summary>Version of the Sonora.Audio assembly under test.</summary>
    public string SonoraVersion { get; init; } = string.Empty;

    /// <summary>Version of the test player.</summary>
    public string TestPlayerVersion { get; init; } = string.Empty;

    /// <summary>Captures the current environment.</summary>
    public static EnvironmentInfoDto Capture() => new()
    {
        Os = RuntimeInformation.OSDescription,
        Runtime = RuntimeInformation.FrameworkDescription,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        SonoraVersion = VersionOf(typeof(AudioEngine).Assembly),
        TestPlayerVersion = VersionOf(typeof(DiagnosticSnapshot).Assembly),
    };

    private static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}

/// <summary>
/// Aggregate diagnostic report: engine state, DSP state, process-level resources, recent events and
/// manual scenario results. The payload exported by <see cref="DiagnosticExporter"/>.
/// </summary>
public sealed record DiagnosticSnapshot
{
    /// <summary>UTC capture time.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Host environment.</summary>
    public EnvironmentInfoDto Environment { get; init; } = new();

    /// <summary>Player and queue state.</summary>
    public EngineSnapshotDto? Engine { get; init; }

    /// <summary>DSP rack state, when available.</summary>
    public DspStateDto? Dsp { get; init; }

    /// <summary>Process-level resource sample (whole process, not Sonora-attributable).</summary>
    public ProcessResourceSample? Process { get; init; }

    /// <summary>Retained diagnostic events, oldest first.</summary>
    public IReadOnlyList<DiagnosticEventDto> Events { get; init; } = [];

    /// <summary>Manual scenario results and soak summary.</summary>
    public ScenarioReportDto? Scenarios { get; init; }

    /// <summary>Total events ever recorded (can exceed <see cref="Events"/> length when the ring wrapped).</summary>
    public long TotalEventsRecorded { get; init; }

    /// <summary>Assembles a snapshot from the live components.</summary>
    public static DiagnosticSnapshot Capture(
        EngineSnapshotDto? engine,
        DspStateDto? dsp,
        ProcessResourceSample? process,
        DiagnosticEventStore store,
        ScenarioReportDto? scenarios)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new DiagnosticSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Environment = EnvironmentInfoDto.Capture(),
            Engine = engine,
            Dsp = dsp,
            Process = process,
            Events = store.Snapshot(),
            Scenarios = scenarios,
            TotalEventsRecorded = store.TotalAdded,
        };
    }
}
