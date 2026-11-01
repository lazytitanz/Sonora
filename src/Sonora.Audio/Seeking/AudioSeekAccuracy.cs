namespace Sonora.Audio.Seeking;

/// <summary>
/// Classifies how precisely a seek established the first retained source PCM frame.
/// </summary>
public enum AudioSeekAccuracy
{
    /// <summary>
    /// The first retained source frame corresponds to the requested frame
    /// within the documented frame-rounding convention.
    /// </summary>
    Exact,

    /// <summary>
    /// The decoder repositioned near the request. <see cref="AudioSeekResult.ActualPosition"/>
    /// is set only when supported by meaningful decoder evidence (not merely a container timestamp).
    /// </summary>
    Approximate,

    /// <summary>
    /// Reposition was attempted, but the actual decoded landing could not be established.
    /// <see cref="AudioSeekResult.ActualPosition"/> is <see langword="null"/>.
    /// </summary>
    Unknown,
}
