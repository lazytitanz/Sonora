using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Converts decoded float PCM to a session format using NAudio channel mixers and WDL resampling.
/// Runs only on the decode pump — never on the WASAPI callback.
/// Ownership: this type owns <see cref="_inner"/> for its lifetime. Provider graphs must not dispose the inner decoder.
/// </summary>
internal sealed class WdlFormatConvertingDecoder : IAudioDecoder
{
    private readonly IAudioDecoder _inner;
    private readonly AudioFormat _target;
    private readonly bool _resamples;
    private ISampleProvider _provider;
    private IDisposable? _ownedMixer;
    private bool _disposed;

    public WdlFormatConvertingDecoder(IAudioDecoder inner, AudioFormat targetFormat)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (targetFormat.SampleFormat != SampleFormat.Float32)
        {
            throw new ArgumentException("Target format must be float32.", nameof(targetFormat));
        }

        _target = targetFormat;
        _resamples = inner.Format.SampleRate != targetFormat.SampleRate;
        var built = BuildProviderGraph();
        _provider = built.Provider;
        _ownedMixer = built.OwnedMixer;
    }

    public AudioFormat Format => _target;

    public TimeSpan Duration => _inner.Duration;

    public bool HasKnownDuration => _inner.HasKnownDuration;

    public bool CanSeek => _inner.CanSeek;

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Capture provider once so a concurrent Seek swap cannot dispose mid-read.
        var provider = Volatile.Read(ref _provider);
        return provider.Read(destination);
    }

    public AudioSeekResult Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var innerResult = _inner.Seek(position);

        ISampleProvider newProvider;
        IDisposable? newMixer;
        try
        {
            var built = BuildProviderGraph();
            newProvider = built.Provider;
            newMixer = built.OwnedMixer;
        }
        catch
        {
            // Leave the previous provider intact so the decode pump never observes a disposed graph.
            throw;
        }

        var oldMixer = Interlocked.Exchange(ref _ownedMixer, newMixer);
        Volatile.Write(ref _provider, newProvider);
        oldMixer?.Dispose();

        return DemoteIfResampling(innerResult);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ownedMixer?.Dispose();
        _ownedMixer = null;
        _inner.Dispose();
    }

    private (ISampleProvider Provider, IDisposable? OwnedMixer) BuildProviderGraph()
    {
        ISampleProvider provider = new DecoderSampleProvider(_inner);
        IDisposable? ownedMixer = null;

        var srcCh = _inner.Format.ChannelCount;
        var dstCh = _target.ChannelCount;
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
            // Core mixer owns nothing about _inner (disposeInner: false); we own the mixer wrapper.
            var mixed = new ChannelMixingDecoder(_inner, AudioFormat.CreateFloat32(_inner.Format.SampleRate, dstCh), disposeInner: false);
            ownedMixer = mixed;
            provider = new DecoderSampleProvider(mixed);
        }

        if (provider.WaveFormat.SampleRate != _target.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, _target.SampleRate);
        }

        return (provider, ownedMixer);
    }

    private AudioSeekResult DemoteIfResampling(AudioSeekResult inner)
    {
        if (!_resamples || inner.Accuracy != AudioSeekAccuracy.Exact || inner.ActualPosition is null)
        {
            return inner;
        }

        // Resampling introduces fractional-frame uncertainty at the session boundary.
        return AudioSeekResult.Approximate(inner.RequestedPosition, inner.ActualPosition.Value);
    }
}
