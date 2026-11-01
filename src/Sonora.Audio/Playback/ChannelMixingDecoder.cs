using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Converts channel count while preserving sample rate. Used when rates already match
/// or as a core-only path for tests without platform resamplers.
/// </summary>
internal sealed class ChannelMixingDecoder : IAudioDecoder
{
    private readonly IAudioDecoder _inner;
    private readonly AudioFormat _target;
    private readonly float[] _scratch;
    private bool _disposeInner;

    public ChannelMixingDecoder(IAudioDecoder inner, AudioFormat targetFormat, bool disposeInner = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (inner.Format.SampleRate != targetFormat.SampleRate)
        {
            throw new ArgumentException(
                "ChannelMixingDecoder requires matching sample rates. Use a platform format converter for resampling.",
                nameof(targetFormat));
        }

        if (targetFormat.SampleFormat != SampleFormat.Float32)
        {
            throw new ArgumentException("Target format must be float32.", nameof(targetFormat));
        }

        _target = targetFormat;
        _disposeInner = disposeInner;
        _scratch = new float[Math.Max(4096, targetFormat.ChannelCount * 256)];
    }

    public AudioFormat Format => _target;
    public TimeSpan Duration => _inner.Duration;
    public bool CanSeek => _inner.CanSeek;

    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        var srcCh = _inner.Format.ChannelCount;
        var dstCh = _target.ChannelCount;
        if (srcCh == dstCh)
        {
            return _inner.Read(destination);
        }

        // Read whole source frames that fit into destination frames.
        var dstFrames = destination.Length / dstCh;
        if (dstFrames <= 0)
        {
            return 0;
        }

        var srcNeeded = dstFrames * srcCh;
        var scratch = _scratch.AsSpan(0, Math.Min(_scratch.Length, srcNeeded));
        var srcFramesCapacity = scratch.Length / srcCh;
        var framesToRequest = Math.Min(dstFrames, srcFramesCapacity);
        var read = _inner.Read(scratch[..(framesToRequest * srcCh)]);
        if (read <= 0)
        {
            return 0;
        }

        var frames = read / srcCh;
        MixChannels(scratch[..read], srcCh, destination[..(frames * dstCh)], dstCh, frames);
        return frames * dstCh;
    }

    public void Seek(TimeSpan position) => _inner.Seek(position);

    public void Dispose()
    {
        if (_disposeInner)
        {
            _inner.Dispose();
            _disposeInner = false;
        }
    }

    internal static void MixChannels(
        ReadOnlySpan<float> source,
        int sourceChannels,
        Span<float> destination,
        int destinationChannels,
        int frames)
    {
        if (sourceChannels == destinationChannels)
        {
            source[..(frames * sourceChannels)].CopyTo(destination);
            return;
        }

        for (var f = 0; f < frames; f++)
        {
            if (sourceChannels == 1 && destinationChannels == 2)
            {
                var s = source[f];
                destination[f * 2] = s;
                destination[f * 2 + 1] = s;
            }
            else if (sourceChannels == 2 && destinationChannels == 1)
            {
                destination[f] = 0.5f * (source[f * 2] + source[f * 2 + 1]);
            }
            else
            {
                // Generic: copy min channels, zero-pad or drop extras.
                var copy = Math.Min(sourceChannels, destinationChannels);
                for (var c = 0; c < copy; c++)
                {
                    destination[f * destinationChannels + c] = source[f * sourceChannels + c];
                }

                for (var c = copy; c < destinationChannels; c++)
                {
                    destination[f * destinationChannels + c] = 0f;
                }
            }
        }
    }
}
