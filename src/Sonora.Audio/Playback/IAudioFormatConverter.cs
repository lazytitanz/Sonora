using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Converts a decoded PCM stream to a target session format (sample rate and/or channels).
/// Must not be used on real-time output callbacks.
/// </summary>
public interface IAudioFormatConverter
{
    /// <summary>
    /// Returns a decoder that produces <paramref name="targetFormat"/> PCM.
    /// May return <paramref name="source"/> when already matching.
    /// The returned decoder owns disposal of <paramref name="source"/> when it wraps it.
    /// </summary>
    IAudioDecoder Convert(IAudioDecoder source, AudioFormat targetFormat);
}
