namespace Sonora.Audio.Output;

/// <summary>
/// Immutable preferred output configuration for an <see cref="AudioPlayer"/>.
/// </summary>
public sealed class AudioOutputConfiguration
{
    /// <summary>Shared-mode default with no exclusive fallback.</summary>
    public static AudioOutputConfiguration Default { get; } = new();

    /// <summary>Preferred output mode. Exclusive must be set explicitly. Default: Shared.</summary>
    public AudioOutputMode Mode { get; init; } = AudioOutputMode.Shared;

    /// <summary>Fallback policy when exclusive open/Init fails recoverably. Default: None (strict).</summary>
    public ExclusiveModeFallback ExclusiveFallback { get; init; } = ExclusiveModeFallback.None;

    /// <summary>
    /// Preferred output latency target when the backend honors it; <see langword="null"/> uses backend defaults.
    /// Treated as a request — endpoints may use a different effective period.
    /// Must be between 5 ms and 500 ms when specified.
    /// </summary>
    public TimeSpan? RequestedLatency
    {
        get;
        init
        {
            if (value is { } latency
                && (latency < TimeSpan.FromMilliseconds(5) || latency > TimeSpan.FromMilliseconds(500)))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RequestedLatency),
                    value,
                    "Requested latency must be between 5 ms and 500 ms when specified.");
            }

            field = value;
        }
    }
}
