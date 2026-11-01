using Sonora.Audio.Formats;

namespace Sonora.Audio.Output;

/// <summary>
/// Parameters for creating a platform output instance.
/// </summary>
/// <param name="DeviceId">
/// Stable endpoint id, or <see langword="null"/> for the platform default render endpoint.
/// </param>
/// <param name="Mode">Requested share mode.</param>
/// <param name="RequestedLatency">Optional latency target.</param>
/// <param name="PreferredFormat">
/// Preferred session PCM format (rate/channels) used for exclusive negotiation hints.
/// </param>
public readonly record struct AudioOutputCreateRequest(
    string? DeviceId,
    AudioOutputMode Mode = AudioOutputMode.Shared,
    TimeSpan? RequestedLatency = null,
    AudioFormat? PreferredFormat = null)
{
    /// <summary>Shared-mode request for the given endpoint.</summary>
    public static AudioOutputCreateRequest Shared(string? deviceId, TimeSpan? requestedLatency = null) =>
        new(deviceId, AudioOutputMode.Shared, requestedLatency);
}
