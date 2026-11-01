namespace Sonora.Audio.Output;

/// <summary>
/// Host output share mode. Shared is the default; exclusive must be requested explicitly.
/// </summary>
public enum AudioOutputMode
{
    /// <summary>Mix with other applications (platform default).</summary>
    Shared = 0,

    /// <summary>Request sole access to the render endpoint when the host supports it.</summary>
    Exclusive = 1,
}
