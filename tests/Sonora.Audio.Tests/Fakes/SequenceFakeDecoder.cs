using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Tests.Fakes;

/// <summary>
/// Decoder that emits a known interleaved float sequence for gapless join assertions.
/// </summary>
internal sealed class SequenceFakeDecoder : IAudioDecoder
{
    private readonly float[] _samples;
    private int _position;
    private bool _disposed;

    public SequenceFakeDecoder(AudioFormat format, float[] samples)
    {
        Format = format;
        _samples = samples;
        Duration = format.SamplesToTime(samples.Length);
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; }
    public bool CanSeek => true;

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var remaining = _samples.Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var toCopy = Math.Min(remaining, destination.Length);
        _samples.AsSpan(_position, toCopy).CopyTo(destination);
        _position += toCopy;
        return toCopy;
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _position = (int)Math.Min(Format.TimeToSamples(position), _samples.Length);
    }

    public void Dispose() => _disposed = true;
}
