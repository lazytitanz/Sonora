namespace Sonora.Audio.Output;

/// <summary>
/// Why shared-mode output is (or was) active despite an exclusive preference,
/// or why a strict exclusive attempt failed.
/// </summary>
public enum ExclusiveFallbackReason
{
    /// <summary>No fallback is active and the last exclusive attempt did not fail for a classified reason.</summary>
    None = 0,

    /// <summary>Another client holds the endpoint (device busy / in use).</summary>
    DeviceBusy = 1,

    /// <summary>The endpoint or policy disallows exclusive access.</summary>
    ExclusiveModeRestricted = 2,

    /// <summary>No supported exclusive format could be negotiated.</summary>
    UnsupportedFormat = 3,

    /// <summary>Exclusive open or initialization failed for another recoverable reason.</summary>
    InitializationFailed = 4,
}
