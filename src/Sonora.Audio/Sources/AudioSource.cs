namespace Sonora.Audio.Sources;

/// <summary>
/// Platform-neutral description of an audio input. Prefer typed sources over interpreting
/// arbitrary strings as URLs.
/// </summary>
public abstract record AudioSource
{
    /// <summary>
    /// Stable display/reopen identifier: filesystem path or absolute URI.
    /// </summary>
    public abstract string Identifier { get; }
}
