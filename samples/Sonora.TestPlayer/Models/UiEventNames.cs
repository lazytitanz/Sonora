namespace Sonora.TestPlayer.Models;

/// <summary>Names of events pushed from the backend to the web UI.</summary>
public static class UiEventNames
{
    /// <summary>Full <see cref="EngineSnapshotDto"/> after a meaningful state change.</summary>
    public const string Snapshot = "engine.snapshot";

    /// <summary>Periodic lightweight position update.</summary>
    public const string Position = "playback.position";

    /// <summary>The current track changed (load, gapless transition, or queue advance).</summary>
    public const string TrackChanged = "playback.trackChanged";

    /// <summary>Natural playback completion (queue ended or advance performed).</summary>
    public const string Completed = "playback.completed";

    /// <summary>Queue contents changed.</summary>
    public const string QueueChanged = "queue.changed";

    /// <summary>A diagnostic event was recorded.</summary>
    public const string DiagnosticEvent = "diagnostics.event";

    /// <summary>Process resource sample.</summary>
    public const string Resources = "diagnostics.resources";

    /// <summary>Device list or output state changed.</summary>
    public const string DevicesChanged = "devices.changed";

    /// <summary>Output configuration or effective mode changed.</summary>
    public const string OutputChanged = "output.changed";

    /// <summary>Network transport status changed.</summary>
    public const string NetworkChanged = "network.changed";

    /// <summary>An engine error was reported.</summary>
    public const string Error = "playback.error";
}
