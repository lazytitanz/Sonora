namespace Sonora.Audio.Devices;

/// <summary>
/// Arguments for <see cref="AudioPlayer.OutputDeviceChanged"/>.
/// </summary>
public sealed class OutputDeviceChangedEventArgs : EventArgs
{
    /// <summary>Creates event args.</summary>
    public OutputDeviceChangedEventArgs(
        string? preferredDeviceId,
        string? activeDeviceId,
        OutputDeviceStatus status,
        OutputRecoveryIntent recoveryIntent,
        OutputDeviceChangeReason reason)
    {
        PreferredDeviceId = preferredDeviceId;
        ActiveDeviceId = activeDeviceId;
        Status = status;
        RecoveryIntent = recoveryIntent;
        Reason = reason;
    }

    /// <summary>Application-preferred endpoint id; <see langword="null"/> means follow system default.</summary>
    public string? PreferredDeviceId { get; }

    /// <summary>Currently bound endpoint id, when known.</summary>
    public string? ActiveDeviceId { get; }

    /// <summary>Current device-binding health.</summary>
    public OutputDeviceStatus Status { get; }

    /// <summary>Whether playback should resume when output returns.</summary>
    public OutputRecoveryIntent RecoveryIntent { get; }

    /// <summary>Why this notification was raised.</summary>
    public OutputDeviceChangeReason Reason { get; }
}
