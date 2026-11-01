using NAudio.Wave;
using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Adapts <see cref="IPcmSampleSource"/> to NAudio's <see cref="ISampleProvider"/>.
/// Reads only buffered PCM — no decoding or disk I/O.
/// </summary>
internal sealed class PcmSampleProviderAdapter : ISampleProvider
{
    private IPcmSampleSource? _source;
    private readonly WaveFormat _waveFormat;

    public PcmSampleProviderAdapter(IPcmSampleSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        var format = source.Format;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.ChannelCount);
    }

    public WaveFormat WaveFormat => _waveFormat;

    /// <summary>
    /// Prevents any further reads of the PCM source (used during output shutdown).
    /// </summary>
    public void Disconnect() => Volatile.Write(ref _source, null);

    public int Read(Span<float> buffer)
    {
        var source = Volatile.Read(ref _source);
        if (source is null || source.IsEndOfStream)
        {
            return 0;
        }

        return source.Read(buffer);
    }
}
