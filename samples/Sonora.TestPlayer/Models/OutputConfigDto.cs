using Sonora.Audio;
using Sonora.Audio.Devices;
using Sonora.Audio.Output;

namespace Sonora.TestPlayer.Models;

/// <summary>
/// Requested output configuration (settable from the UI) plus the effective state reported by the player.
/// Only the "requested" members are read when the UI submits a configuration; the "effective"
/// members are informational and ignored on input.
/// </summary>
public sealed record OutputConfigDto
{
    // ---- Requested (input) ----

    /// <summary>Requested share mode. Default: shared.</summary>
    public AudioOutputMode Mode { get; init; } = AudioOutputMode.Shared;

    /// <summary>Fallback policy when an exclusive open fails recoverably. Default: none (strict).</summary>
    public ExclusiveModeFallback ExclusiveFallback { get; init; } = ExclusiveModeFallback.None;

    /// <summary>Requested latency in milliseconds (5–500), or <see langword="null"/> for backend default.</summary>
    public double? RequestedLatencyMs { get; init; }

    // ---- Effective (output only) ----

    /// <summary>Mode of the currently attached output, or <see langword="null"/> when none is attached.</summary>
    public AudioOutputMode? EffectiveMode { get; init; }

    /// <summary>Why shared fallback is active or why the last exclusive attempt was classified.</summary>
    public ExclusiveFallbackReason FallbackReason { get; init; }

    /// <summary>Device-binding health.</summary>
    public OutputDeviceStatus DeviceStatus { get; init; }

    /// <summary>Application-preferred endpoint id; <see langword="null"/> follows the system default.</summary>
    public string? PreferredDeviceId { get; init; }

    /// <summary>Currently bound endpoint id, when known.</summary>
    public string? ActiveDeviceId { get; init; }

    /// <summary>Whether playback resumes when output returns after device loss.</summary>
    public OutputRecoveryIntent RecoveryIntent { get; init; }

    /// <summary>Backend-reported effective latency in milliseconds when known (from the last configuration event).</summary>
    public double? EffectiveLatencyMs { get; init; }

    /// <summary>
    /// Converts the requested members into a Sonora configuration.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The latency is outside 5–500 ms.</exception>
    public AudioOutputConfiguration ToConfiguration()
    {
        return new AudioOutputConfiguration
        {
            Mode = Mode,
            ExclusiveFallback = ExclusiveFallback,
            RequestedLatency = RequestedLatencyMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
        };
    }

    /// <summary>Builds a DTO from the player's current requested and effective output state.</summary>
    public static OutputConfigDto FromPlayer(AudioPlayer player, double? effectiveLatencyMs = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        var requested = player.OutputConfiguration;
        return new OutputConfigDto
        {
            Mode = requested.Mode,
            ExclusiveFallback = requested.ExclusiveFallback,
            RequestedLatencyMs = requested.RequestedLatency?.TotalMilliseconds,
            EffectiveMode = player.EffectiveOutputMode,
            FallbackReason = player.OutputFallbackReason,
            DeviceStatus = player.OutputDeviceStatus,
            PreferredDeviceId = player.PreferredOutputDeviceId,
            ActiveDeviceId = player.ActiveOutputDeviceId,
            RecoveryIntent = player.OutputRecoveryIntent,
            EffectiveLatencyMs = effectiveLatencyMs,
        };
    }
}
