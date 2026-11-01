using NAudio.Wave;
using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Adapts <see cref="IPcmSampleSource"/> to NAudio's <see cref="ISampleProvider"/>.
/// Reads only buffered PCM — no decoding or disk I/O.
/// </summary>
internal sealed class PcmSampleProviderAdapter : ISampleProvider
{
    private readonly IPcmSampleSource _source;
    private readonly WaveFormat _waveFormat;

    public PcmSampleProviderAdapter(IPcmSampleSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        var format = source.Format;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.ChannelCount);
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(Span<float> buffer)
    {
        if (_source.IsEndOfStream)
        {
            return 0;
        }

        return _source.Read(buffer);
    }
}
