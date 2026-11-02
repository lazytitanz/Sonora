using Sonora.Audio.Devices;

namespace Sonora.TestPlayer.Models;

/// <summary>JSON-friendly output endpoint description.</summary>
/// <param name="Id">Stable host endpoint identifier.</param>
/// <param name="Name">Friendly display name.</param>
/// <param name="IsDefault">Whether this is the system default render endpoint (Multimedia role).</param>
/// <param name="State">Host-reported availability state.</param>
/// <param name="IsPreferred">Whether the application explicitly selected this endpoint.</param>
/// <param name="IsActive">Whether the player is currently bound to this endpoint.</param>
public sealed record DeviceDto(
    string Id,
    string Name,
    bool IsDefault,
    AudioDeviceState State,
    bool IsPreferred,
    bool IsActive)
{
    /// <summary>Creates a DTO from a Sonora device snapshot and the player's current bindings.</summary>
    public static DeviceDto From(AudioOutputDevice device, string? preferredId, string? activeId)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceDto(
            device.Id,
            device.Name,
            device.IsDefault,
            device.State,
            string.Equals(device.Id, preferredId, StringComparison.Ordinal),
            string.Equals(device.Id, activeId, StringComparison.Ordinal));
    }
}
