using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Windows format converter: identity, channel mix, and NAudio WDL resampling as needed.
/// </summary>
public sealed class WindowsAudioFormatConverter : IAudioFormatConverter
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

        if (source.Format.SampleRate == targetFormat.SampleRate)
        {
            return new ChannelMixingDecoder(source, targetFormat);
        }

        return new WdlFormatConvertingDecoder(source, targetFormat);
    }
}
