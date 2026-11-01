namespace Sonora.Audio.DSP;

/// <summary>
/// A reusable PCM effect owned by a player effect rack.
/// </summary>
/// <remarks>
/// Implementations must be safe for real-time processing: no allocations, I/O, or blocking
/// inside <see cref="IAudioEffectRuntime.Process"/>. Parameter changes are applied on the
/// audio thread at block boundaries via pending-update handoff.
/// </remarks>
public interface IAudioEffect : IDisposable
{
    /// <summary>Short display name for diagnostics and UI.</summary>
    string Name { get; }

    /// <summary>
    /// When false, the effect is skipped. Bypass transitions should be click-free where the backend supports it.
    /// </summary>
    bool Enabled { get; set; }
}
