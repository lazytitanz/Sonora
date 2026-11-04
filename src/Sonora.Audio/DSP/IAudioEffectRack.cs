namespace Sonora.Audio.DSP;

/// <summary>
/// Ordered per-player effect chain. Structural edits are thread-safe relative to audio processing
/// via immutable snapshots; parameter updates are applied on the audio thread at block boundaries.
/// </summary>
public interface IAudioEffectRack
{
    /// <summary>
    /// When true, the rack outputs unprocessed (dry) audio. Effects continue to receive PCM so
    /// filter and dynamics state stay current for immediate reactivation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transitions use a short linear crossfade. Dry audio is delayed to match the effective wet-path
    /// latency (for example limiter look-ahead), so bypass does not jump the output timeline relative
    /// to the processed path. While bypassed, the rack still incurs the CPU cost of the active chain.
    /// </para>
    /// <para>
    /// Steady-state bypassed output therefore has the same DSP latency as the wet chain. Seek/load/reset
    /// clear delay-line state under the existing effects barrier.
    /// </para>
    /// </remarks>
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
