using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioDecoder : IAudioDecoder
{
    private readonly float[] _samples;
    private int _position;
    private bool _disposed;
    private readonly TimeSpan? _readDelay;

    public FakeAudioDecoder(AudioFormat format, float[] samples, bool canSeek = true, TimeSpan? readDelay = null)
    {
        Format = format;
        _samples = samples;
        CanSeek = canSeek;
        Duration = format.SamplesToTime(samples.Length);
        _readDelay = readDelay;
    }

    /// <summary>
    /// When set, <see cref="Seek"/> returns this accuracy after applying the configured landing policy.
    /// Defaults to Exact at the frame-rounded request.
    /// </summary>
    public AudioSeekAccuracy SeekAccuracy { get; set; } = AudioSeekAccuracy.Exact;

    /// <summary>
    /// Optional early landing offset applied before discard simulation (source frames).
    /// Used to exercise Approximate/Exact discard behavior.
    /// </summary>
    public long SeekEarlyFrames { get; set; }

    public static FakeAudioDecoder CreateSilence(AudioFormat format, TimeSpan duration)
    {
        var count = (int)format.TimeToSamples(duration);
        return new FakeAudioDecoder(format, new float[count]);
    }

    public static FakeAudioDecoder CreateTone(AudioFormat format, TimeSpan duration, float frequencyHz = 440f)
    {
        var count = (int)format.TimeToSamples(duration);
        var samples = new float[count];
        for (var i = 0; i < count; i += format.ChannelCount)
        {
            var t = (i / format.ChannelCount) / (double)format.SampleRate;
            var sample = (float)Math.Sin(2 * Math.PI * frequencyHz * t) * 0.2f;
            for (var ch = 0; ch < format.ChannelCount; ch++)
            {
                samples[i + ch] = sample;
            }
        }

        return new FakeAudioDecoder(format, samples);
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; }
    public bool HasKnownDuration { get; init; } = true;
    public bool CanSeek { get; }

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readDelay is not null)
        {
            Thread.Sleep(_readDelay.Value);
        }

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

    public AudioSeekResult Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek)
        {
            throw new NotSupportedException();
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (position > Duration)
        {
            position = Duration;
        }

        var requestedFrames = Format.TimeToFrames(position);
        var maxFrames = Format.TimeToFrames(Duration);
        requestedFrames = AudioFormat.ClampFrameIndex(requestedFrames, maxFrames);
        var requested = Format.FramesToTime(requestedFrames);

        if (SeekAccuracy == AudioSeekAccuracy.Unknown)
        {
            _position = (int)Math.Min(requestedFrames * Format.ChannelCount, _samples.Length);
            return AudioSeekResult.Unknown(requested);
        }

        var landingFrames = Math.Max(0, requestedFrames - Math.Max(0, SeekEarlyFrames));
        _position = (int)Math.Min(landingFrames * Format.ChannelCount, _samples.Length);

        // Simulate forward discard to the request when we landed early.
        if (landingFrames < requestedFrames)
        {
            _position = (int)Math.Min(requestedFrames * Format.ChannelCount, _samples.Length);
            landingFrames = requestedFrames;
        }

        var actual = Format.FramesToTime(landingFrames);
        return SeekAccuracy == AudioSeekAccuracy.Approximate
            ? AudioSeekResult.Approximate(requested, actual)
            : AudioSeekResult.Exact(requested, actual);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
