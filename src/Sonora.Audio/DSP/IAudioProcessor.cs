using Sonora.Audio.Formats;

namespace Sonora.Audio.DSP;

/// <summary>
/// Processes interleaved float32 PCM on the decode-pump path (not on real-time output callbacks).
/// </summary>
public interface IAudioProcessor
{
    /// <summary>
    /// Processes <paramref name="buffer"/> in place.
    /// </summary>
    void Process(Span<float> buffer, in AudioFormat format);
}
