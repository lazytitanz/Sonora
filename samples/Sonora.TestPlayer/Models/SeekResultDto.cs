using Sonora.Audio.Seeking;

namespace Sonora.TestPlayer.Models;

/// <summary>JSON-friendly projection of <see cref="AudioSeekResult"/>.</summary>
/// <param name="RequestedMs">Clamped, frame-rounded requested position in milliseconds.</param>
/// <param name="ActualMs">Decoder-evidenced landing position, or <see langword="null"/> when unknown.</param>
/// <param name="Accuracy">How precisely the landing was established.</param>
/// <param name="DeltaMs">Signed difference (actual − requested) when <paramref name="ActualMs"/> is known.</param>
public sealed record SeekResultDto(
    double RequestedMs,
    double? ActualMs,
    AudioSeekAccuracy Accuracy,
    double? DeltaMs)
{
    /// <summary>Creates a DTO from an engine seek result.</summary>
    public static SeekResultDto From(AudioSeekResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var requested = result.RequestedPosition.TotalMilliseconds;
        var actual = result.ActualPosition?.TotalMilliseconds;
        return new SeekResultDto(requested, actual, result.Accuracy, actual - requested);
    }
}
