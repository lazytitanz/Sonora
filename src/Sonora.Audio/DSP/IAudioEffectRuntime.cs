using Sonora.Audio.Formats;

namespace Sonora.Audio.DSP;

/// <summary>
/// Internal real-time processing surface for effects. Not part of the public API.
/// </summary>
internal interface IAudioEffectRuntime : IAudioEffect
{
    /// <summary>Prepares the effect for the given interleaved float32 format.</summary>
    void Configure(in AudioFormat format);

    /// <summary>Clears internal filter/dynamics state without changing user parameters.</summary>
    void Reset();

    /// <summary>
    /// Applies queued parameter updates. Called only on the audio processing thread
    /// at the start of a processing block (never from the control thread).
    /// </summary>
    void ApplyPendingUpdates();

    /// <summary>Processes interleaved float32 PCM in place.</summary>
    void Process(Span<float> buffer, in AudioFormat format);

    /// <summary>
    /// Effective output latency in frames for the current configuration and enabled state.
    /// Read on the audio thread after <see cref="ApplyPendingUpdates"/>.
    /// </summary>
    int LatencySamples { get; }

    /// <summary>
    /// Configured maximum latency in frames if the effect is enabled (0 for zero-latency effects).
    /// Used by the rack to validate chain bounds; independent of the current <see cref="IAudioEffect.Enabled"/> flag.
    /// </summary>
    int MaxLatencySamples { get; }
}
