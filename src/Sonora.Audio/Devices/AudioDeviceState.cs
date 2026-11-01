namespace Sonora.Audio.Devices;

/// <summary>
/// Availability state of an audio endpoint as reported by the host platform.
/// </summary>
public enum AudioDeviceState
{
    /// <summary>Endpoint is present and usable.</summary>
    Active = 0,

    /// <summary>Endpoint is present but disabled by the user or system.</summary>
    Disabled = 1,

    /// <summary>Endpoint is known but currently unplugged.</summary>
    Unplugged = 2,

    /// <summary>Endpoint is not present.</summary>
    NotPresent = 3,

    /// <summary>State could not be determined.</summary>
    Unknown = 4,
}
