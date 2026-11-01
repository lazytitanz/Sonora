namespace Sonora.Audio.Devices;

/// <summary>
/// Arguments for <see cref="IAudioOutputDevices.DefaultDeviceChanged"/>.
/// </summary>
public sealed class DefaultOutputDeviceChangedEventArgs : EventArgs
{
    /// <summary>Creates event args.</summary>
    public DefaultOutputDeviceChangedEventArgs(string? previousDeviceId, string? newDeviceId)
    {
        PreviousDeviceId = previousDeviceId;
        NewDeviceId = newDeviceId;
    }

    /// <summary>Previous default endpoint id, if known.</summary>
    public string? PreviousDeviceId { get; }

    /// <summary>New default endpoint id, if available.</summary>
    public string? NewDeviceId { get; }
}
