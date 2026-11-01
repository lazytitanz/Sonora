using NAudio.Wave;
using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Adapts <see cref="IAudioDecoder"/> to NAudio <see cref="ISampleProvider"/> for WDL resampling.
/// </summary>
internal sealed class DecoderSampleProvider : ISampleProvider
{
    private readonly IAudioDecoder _decoder;

    public DecoderSampleProvider(IAudioDecoder decoder)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        var f = decoder.Format;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.ChannelCount);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer) => _decoder.Read(buffer);
}
