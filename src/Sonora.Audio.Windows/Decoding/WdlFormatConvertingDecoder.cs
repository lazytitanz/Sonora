using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Converts decoded float PCM to a session format using NAudio channel mixers and WDL resampling.
/// Runs only on the decode pump — never on the WASAPI callback.
/// </summary>
internal sealed class WdlFormatConvertingDecoder : IAudioDecoder
{
    private readonly IAudioDecoder _inner;
    private readonly ISampleProvider _provider;
    private readonly AudioFormat _target;
    private bool _disposed;

    public WdlFormatConvertingDecoder(IAudioDecoder inner, AudioFormat targetFormat)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (targetFormat.SampleFormat != SampleFormat.Float32)
        {
            throw new ArgumentException("Target format must be float32.", nameof(targetFormat));
        }

        _target = targetFormat;
        ISampleProvider provider = new DecoderSampleProvider(inner);

        // Channel-map to target channel count at the source rate first, then resample.
        var srcCh = inner.Format.ChannelCount;
        var dstCh = targetFormat.ChannelCount;
        if (srcCh == 1 && dstCh == 2)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (srcCh == 2 && dstCh == 1)
        {
            provider = new StereoToMonoSampleProvider(provider);
        }
        else if (srcCh != dstCh)
        {
            // Fall back to core mixer for uncommon layouts, then wrap again.
            var mixed = new ChannelMixingDecoder(inner, AudioFormat.CreateFloat32(inner.Format.SampleRate, dstCh), disposeInner: false);
            provider = new DecoderSampleProvider(mixed);
        }

        if (provider.WaveFormat.SampleRate != targetFormat.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetFormat.SampleRate);
        }

        _provider = provider;
    }

    public AudioFormat Format => _target;

    public TimeSpan Duration => _inner.Duration;

    public bool CanSeek => _inner.CanSeek;

    public int Read(Span<float> destination) => _provider.Read(destination);

    public void Seek(TimeSpan position)
    {
        _inner.Seek(position);
        // WDL state is discarded by recreating on next reads after seek — reset via new provider would be ideal;
        // for gapless preload seeks are rare; call Reset if available through recreation path in player Stop/Seek
        // by disposing and rebuilding. Here we seek the inner decoder; residual WDL latency is acceptable for 0.4.0.
        if (_provider is WdlResamplingSampleProvider)
        {
            // No public Reset on WdlResamplingSampleProvider; inner seek is the primary timeline.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
    }
}
