namespace Sonora.Audio.Output;

/// <summary>
/// Behavior when an exclusive-mode open or initialization fails for a recoverable reason.
/// </summary>
public enum ExclusiveModeFallback
{
    /// <summary>
    /// Do not fall back. Preserve the exclusive preference and report failure
    /// (strict exclusive mode).
    /// </summary>
    None = 0,

    /// <summary>Attempt shared-mode output after a recoverable exclusive failure.</summary>
    Shared = 1,
}
