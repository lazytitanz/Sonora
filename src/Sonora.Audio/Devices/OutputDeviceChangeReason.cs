namespace Sonora.Audio.Devices;

/// <summary>
/// Why an <see cref="AudioPlayer.OutputDeviceChanged"/> notification was raised.
/// </summary>
public enum OutputDeviceChangeReason
{
    /// <summary>The application selected a device via <c>SetOutputDeviceAsync</c>.</summary>
    SelectionChanged = 0,

    /// <summary>Windows (or the host) changed the default playback endpoint while following default.</summary>
    SystemDefaultChanged = 1,

    /// <summary>The preferred explicit device was lost; fell back to the system default.</summary>
    DeviceLostFallback = 2,

    /// <summary>Output became available again after being unavailable.</summary>
    Recovered = 3,

    /// <summary>No usable output endpoint is available.</summary>
    Unavailable = 4,

    /// <summary>An output rebind began; no endpoint is attached while switching.</summary>
    Switching = 5,

    /// <summary>A device-switch request was superseded by another operation.</summary>
    Superseded = 6,
}
