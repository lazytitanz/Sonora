namespace Sonora.Audio.Devices;

/// <summary>
/// Empty device manager used when no platform backend is supplied (tests, non-Windows hosts).
/// </summary>
public sealed class NullAudioOutputDevices : IAudioOutputDevices
{
    /// <summary>Shared instance.</summary>
    public static NullAudioOutputDevices Instance { get; } = new();

    /// <inheritdoc />
    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<DefaultOutputDeviceChangedEventArgs>? DefaultDeviceChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioOutputDevice> GetDevices() => Array.Empty<AudioOutputDevice>();

    /// <inheritdoc />
    public AudioOutputDevice? GetDefaultDevice() => null;

    /// <inheritdoc />
    public bool TryGetDevice(string deviceId, out AudioOutputDevice? device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        device = null;
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
