using Sonora.Audio.Seeking;

namespace Sonora.TestPlayer.Models;

/// <summary>JSON-friendly projection of <see cref="AudioSeekResult"/>.</summary>
/// <param name="RequestedMs">Clamped, frame-rounded requested position in milliseconds.</param>
/// <param name="ActualMs">Decoder-evidenced landing position, or <see langword="null"/> when unknown.</param>
/// <param name="Accuracy">How precisely the landing was established.</param>
/// <param name="DeltaMs">Signed difference (actual − requested) when <paramref name="ActualMs"/> is known.</param>
/// <param name="Coalesced">
/// True when this call was skipped because a newer seek target was already queued (latest-wins).
/// Not the same as <see cref="AudioSeekAccuracy.Unknown"/> from a real decoder landing.
/// </param>
public sealed record SeekResultDto(
    double RequestedMs,
    double? ActualMs,
    AudioSeekAccuracy Accuracy,
    double? DeltaMs,
    bool Coalesced = false)
{
    /// <summary>Creates a DTO from an engine seek result.</summary>
    public static SeekResultDto From(AudioSeekResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var requested = result.RequestedPosition.TotalMilliseconds;
        var actual = result.ActualPosition?.TotalMilliseconds;
        return new SeekResultDto(requested, actual, result.Accuracy, actual - requested, Coalesced: false);
    }

    /// <summary>Creates a placeholder for a seek that lost latest-wins coalescing.</summary>
    public static SeekResultDto CoalescedSkip(double requestedMs) =>
        new(requestedMs, ActualMs: null, AudioSeekAccuracy.Unknown, DeltaMs: null, Coalesced: true);
}
