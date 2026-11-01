using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Core format converter: identity when formats match; channel mix when rates match.
/// Throws when sample-rate conversion is required (use a Windows/platform converter).
/// </summary>
public sealed class PassthroughFormatConverter : IAudioFormatConverter
{
    /// <inheritdoc />
    public IAudioDecoder Convert(IAudioDecoder source, AudioFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (targetFormat.SampleFormat != SampleFormat.Float32)
        {
            throw new ArgumentException("Target format must be float32.", nameof(targetFormat));
        }

        if (source.Format.Equals(targetFormat))
        {
            return source;
        }

        if (source.Format.SampleRate != targetFormat.SampleRate)
        {
            throw new NotSupportedException(
                $"Sample-rate conversion from {source.Format.SampleRate} Hz to {targetFormat.SampleRate} Hz " +
                "requires a platform format converter (e.g. SonoraWindows WDL).");
        }

        return new ChannelMixingDecoder(source, targetFormat);
    }
}
