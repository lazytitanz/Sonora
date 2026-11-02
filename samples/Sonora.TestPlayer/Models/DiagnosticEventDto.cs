namespace Sonora.TestPlayer.Models;

/// <summary>Severity of a diagnostic event.</summary>
public enum DiagnosticLevel
{
    /// <summary>High-volume detail.</summary>
    Debug = 0,

    /// <summary>Normal lifecycle information.</summary>
    Info = 1,

    /// <summary>Unexpected but recoverable.</summary>
    Warning = 2,

    /// <summary>A failure.</summary>
    Error = 3,
}

/// <summary>One recorded diagnostic event.</summary>
/// <param name="Seq">Monotonic sequence number (unique per store, never reused after <c>Clear</c>).</param>
/// <param name="ElapsedMs">High-resolution milliseconds since the store was created (monotonic).</param>
/// <param name="UtcTime">Wall-clock UTC time of the event.</param>
/// <param name="Category">Subsystem, for example <c>playback</c>, <c>device</c>, <c>network</c>, <c>scenario</c>.</param>
/// <param name="Level">Severity.</param>
/// <param name="Name">Short machine-readable event name, for example <c>playback.completed</c>.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="Data">Optional string key/value details.</param>
public sealed record DiagnosticEventDto(
    long Seq,
    double ElapsedMs,
    DateTimeOffset UtcTime,
    string Category,
    DiagnosticLevel Level,
    string Name,
    string Message,
    IReadOnlyDictionary<string, string>? Data = null);
