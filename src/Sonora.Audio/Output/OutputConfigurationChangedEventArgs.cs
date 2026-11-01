namespace Sonora.Audio.Output;

/// <summary>
/// Arguments for <see cref="AudioPlayer.OutputConfigurationChanged"/>.
/// </summary>
public sealed class OutputConfigurationChangedEventArgs : EventArgs
{
    /// <summary>Creates event args.</summary>
    public OutputConfigurationChangedEventArgs(
        AudioOutputConfiguration requested,
        AudioOutputMode? effectiveMode,
        ExclusiveFallbackReason fallbackReason,
        TimeSpan? effectiveLatency)
    {
        Requested = requested ?? throw new ArgumentNullException(nameof(requested));
        EffectiveMode = effectiveMode;
        FallbackReason = fallbackReason;
        EffectiveLatency = effectiveLatency;
    }

    /// <summary>Application-requested configuration snapshot.</summary>
    public AudioOutputConfiguration Requested { get; }

    /// <summary>
    /// Mode of the currently attached output, or <see langword="null"/> when none is attached
    /// (<see cref="Devices.OutputDeviceStatus.Unavailable"/> / <see cref="Devices.OutputDeviceStatus.Switching"/>).
    /// </summary>
    public AudioOutputMode? EffectiveMode { get; }

    /// <summary>Why shared fallback is active, or why the last exclusive attempt was classified.</summary>
    public ExclusiveFallbackReason FallbackReason { get; }

    /// <summary>Backend-reported latency when known.</summary>
    public TimeSpan? EffectiveLatency { get; }
}
