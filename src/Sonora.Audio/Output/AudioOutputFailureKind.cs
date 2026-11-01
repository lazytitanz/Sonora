namespace Sonora.Audio.Output;

/// <summary>
/// Classifies <see cref="AudioOutputException"/> failures without exposing host HRESULTs.
/// </summary>
public enum AudioOutputFailureKind
{
    /// <summary>Unspecified or generic output failure.</summary>
    Unspecified = 0,

    /// <summary>Endpoint is in use by another exclusive (or conflicting) client.</summary>
    DeviceBusy = 1,

    /// <summary>Exclusive mode is not permitted on this endpoint or by policy.</summary>
    ExclusiveModeRestricted = 2,

    /// <summary>Requested or negotiated format is not supported.</summary>
    UnsupportedFormat = 3,

    /// <summary>Endpoint missing, inactive, or otherwise unavailable.</summary>
    DeviceUnavailable = 4,

    /// <summary>Open or Init failed for another reason.</summary>
    InitializationFailed = 5,

    /// <summary>Output could not be quiesced during <see cref="Abstractions.IAudioOutput.Shutdown"/>.</summary>
    ShutdownFailed = 6,
}
