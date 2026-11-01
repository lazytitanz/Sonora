namespace Sonora.Audio.Devices;

/// <summary>
/// Immutable snapshot of an audio output endpoint.
/// </summary>
public sealed class AudioOutputDevice
{
    /// <summary>Creates a device snapshot.</summary>
    public AudioOutputDevice(string id, string name, bool isDefault, AudioDeviceState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(name);

        Id = id;
        Name = name;
        IsDefault = isDefault;
        State = state;
    }

    /// <summary>Stable host endpoint identifier.</summary>
    public string Id { get; }

    /// <summary>Friendly display name.</summary>
    public string Name { get; }

    /// <summary>
    /// Whether this endpoint is the current system default for Sonora's documented default role
    /// (Windows: Multimedia render).
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>Host-reported availability state.</summary>
    public AudioDeviceState State { get; }
}
