using Sonora.Audio.Abstractions;
using Sonora.Audio.Buffers;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Non-blocking PCM source backed by a ring buffer. Safe for audio output callbacks.
/// Applies master volume then the player effect rack after reading dry PCM from the ring
/// so live volume and DSP parameter changes are audible without waiting for the buffer to drain.
/// </summary>
internal sealed class BufferedSampleSource : IPcmSampleSource
{
    private readonly PcmRingBuffer _ring;
    private readonly AudioFormat _format;
    private readonly AudioEffectRack _effects;
    private readonly Func<float> _getVolume;
    private readonly Action<int> _onSamplesConsumed;
    private readonly Action _onUnderrun;
    private readonly Func<bool> _isPlaying;
    private int _endOfStream;

    public BufferedSampleSource(
        PcmRingBuffer ring,
        AudioFormat format,
        AudioEffectRack effects,
        Func<float> getVolume,
        Action<int> onSamplesConsumed,
        Action onUnderrun,
        Func<bool> isPlaying)
    {
        _ring = ring;
        _format = format;
        _effects = effects;
        _getVolume = getVolume;
        _onSamplesConsumed = onSamplesConsumed;
        _onUnderrun = onUnderrun;
        _isPlaying = isPlaying;
    }

    public AudioFormat Format => _format;

    public bool IsEndOfStream => Volatile.Read(ref _endOfStream) == 1;

    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        var read = _ring.TryRead(destination);
        if (read > 0)
        {
            _onSamplesConsumed(read);
            var wet = destination[..read];
            PcmVolume.Apply(wet, _getVolume());
            _effects.Process(wet, _format);
        }

        if (read == destination.Length)
        {
            return read;
        }

        if (_ring.IsCompleted)
        {
            Interlocked.Exchange(ref _endOfStream, 1);
            if (read < destination.Length)
            {
                destination[read..].Clear();
            }

            return read;
        }

        // Shortfall while the producer is still active => underrun episode.
        if (_isPlaying())
        {
            _onUnderrun();
        }

        destination[read..].Clear();
        return destination.Length;
    }

    public void ResetEndOfStream()
    {
        Interlocked.Exchange(ref _endOfStream, 0);
    }
}
