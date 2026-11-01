namespace Sonora.Audio.DSP;

/// <summary>
/// Ordered per-player effect chain. Structural edits are thread-safe relative to audio processing
/// via immutable snapshots; parameter updates are applied on the audio thread at block boundaries.
/// </summary>
public interface IAudioEffectRack
{
    /// <summary>When true, all effects are skipped.</summary>
    bool Bypass { get; set; }

    /// <summary>Current effects in processing order.</summary>
    IReadOnlyList<IAudioEffect> Items { get; }

    /// <summary>Adds a multiband equalizer (default six-band layout when <paramref name="options"/> is null).</summary>
    IEqualizerEffect AddEqualizer(EqualizerOptions? options = null);

    /// <summary>Adds a preamp effect.</summary>
    IPreampEffect AddPreamp(float gainDb = 0f);

    /// <summary>Adds a peak limiter for clipping protection.</summary>
    ILimiterEffect AddLimiter(LimiterOptions? options = null);

    /// <summary>Removes an effect from the rack. Disposal is deferred until it is safe for the audio callback.</summary>
    bool Remove(IAudioEffect effect);

    /// <summary>Moves an effect within the rack.</summary>
    void Move(int fromIndex, int toIndex);

    /// <summary>Removes all effects (deferred disposal while audio may still reference them).</summary>
    void Clear();
}
