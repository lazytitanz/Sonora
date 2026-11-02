using System.Text.Json.Serialization;

namespace Sonora.TestPlayer.Models;

/// <summary>
/// One entry in the application-owned playback queue. Sonora itself has no queue concept;
/// the test player owns ordering and feeds paths to the engine.
/// </summary>
/// <param name="Id">Stable identifier for this queue entry (distinct even when the same file is queued twice).</param>
/// <param name="Path">Local file path, or the absolute URI for a streamed source.</param>
/// <param name="Title">Tag title, or the file name without extension when the tag has none (app-side fallback only).</param>
/// <param name="Artist">Joined track artists, or <see langword="null"/>.</param>
/// <param name="Album">Album title, or <see langword="null"/>.</param>
/// <param name="Duration">Container-reported duration when known (not sample-accurate).</param>
/// <param name="Format">Display label derived from the file extension (for example <c>FLAC</c>); not a codec guarantee.</param>
/// <param name="SampleRate">Tag-reported sample rate in Hz when known.</param>
/// <param name="ChannelCount">Tag-reported channel count when known.</param>
/// <param name="ArtworkDataUrl">
/// Optional <c>data:</c> URL for embedded artwork (size-limited for the UI). Queue listings leave this
/// <see langword="null"/>; request artwork explicitly for the current track.
/// </param>
public sealed record QueueItem(
    string Id,
    string Path,
    string Title,
    string? Artist = null,
    string? Album = null,
    [property: JsonIgnore] TimeSpan? Duration = null,
    string? Format = null,
    int? SampleRate = null,
    int? ChannelCount = null,
    string? ArtworkDataUrl = null)
{
    /// <summary>Duration in milliseconds for JSON consumers, or <see langword="null"/> when unknown.</summary>
    public double? DurationMs => Duration?.TotalMilliseconds;

    /// <summary>Whether this entry is a network stream rather than a local file.</summary>
    public bool IsStream =>
        Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
