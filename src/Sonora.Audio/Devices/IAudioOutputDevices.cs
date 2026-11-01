namespace Sonora.Audio.Devices;

/// <summary>
/// Platform-neutral discovery and change notifications for audio output endpoints.
/// Implementations must never invoke handlers on native device-notification threads;
/// marshal to a thread-pool or equivalent first.
/// </summary>
public interface IAudioOutputDevices : IDisposable
{
    /// <summary>
    /// Returns a snapshot of known render endpoints (including unavailable ones where the host exposes them).
    /// </summary>
    IReadOnlyList<AudioOutputDevice> GetDevices();

    /// <summary>
    /// Returns the current system default render endpoint for Sonora's documented default role,
    /// or <see langword="null"/> when none is available.
    /// </summary>
    AudioOutputDevice? GetDefaultDevice();

    /// <summary>
    /// Looks up a device by stable id in a fresh snapshot.
    /// </summary>
    bool TryGetDevice(string deviceId, out AudioOutputDevice? device);

    /// <summary>
    /// Raised when the set of devices or their states may have changed.
    /// Handlers must not block and must not re-enter engine dispose from the notification path unsafely.
    /// </summary>
    event EventHandler? DevicesChanged;

    /// <summary>
    /// Raised when the system default render endpoint changes for Sonora's documented default role.
    /// </summary>
    event EventHandler<DefaultOutputDeviceChangedEventArgs>? DefaultDeviceChanged;
}
