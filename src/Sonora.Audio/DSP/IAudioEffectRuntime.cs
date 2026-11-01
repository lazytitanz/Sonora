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
}
