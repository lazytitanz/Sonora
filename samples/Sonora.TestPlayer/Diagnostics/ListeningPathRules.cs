namespace Sonora.TestPlayer.Diagnostics;

/// <summary>Pure helpers for verifying promote vs fresh-Load playback paths.</summary>
public static class ListeningPathRules
{
    public static bool IsVerifiedPromote(bool trackTransitionedFired, int transitionId, string? expectedB, string? actualSourcePath) =>
        trackTransitionedFired
        && transitionId > 0
        && PathsEqual(expectedB, actualSourcePath);

    public static bool IsVerifiedFreshLoad(bool trackTransitionedFired, string? expectedB, string? actualSourcePath) =>
        !trackTransitionedFired
        && PathsEqual(expectedB, actualSourcePath);

    public static string SummarizePath(
        string scenario,
        bool trackTransitionedFired,
        int transitionId,
        ListeningClassification listen,
        string pcmSummary) =>
        $"{scenario}: {listen} / {pcmSummary}"
        + (trackTransitionedFired ? $" (TrackTransitioned id={transitionId})" : " (no TrackTransitioned)");

    private static bool PathsEqual(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
