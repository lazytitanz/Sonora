namespace Sonora.Audio.Devices;

/// <summary>
/// Device-binding health for an <see cref="AudioPlayer"/>, independent of <see cref="Formats.PlaybackState"/>.
/// </summary>
public enum OutputDeviceStatus
{
    /// <summary>An output endpoint is bound and usable.</summary>
    Available = 0,

    /// <summary>
    /// The preferred explicit device is unavailable; the player is temporarily using the system default.
    /// </summary>
    PreferredUnavailable = 1,

    /// <summary>No usable output endpoint is currently bound.</summary>
    Unavailable = 2,

    /// <summary>
    /// An output rebind is in progress: the previous endpoint has been detached and no replacement
    /// is attached yet. Distinct from transport <see cref="Formats.PlaybackState"/>.
    /// </summary>
    Switching = 3,
}
