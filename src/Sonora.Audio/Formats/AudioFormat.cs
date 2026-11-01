namespace Sonora.Audio.Formats;

/// <summary>
/// Immutable description of a PCM audio stream.
/// </summary>
/// <param name="SampleRate">Samples per second per channel.</param>
/// <param name="ChannelCount">Number of interleaved channels.</param>
/// <param name="SampleFormat">Numeric sample representation. Sonora prefers <see cref="SampleFormat.Float32"/>.</param>
public readonly record struct AudioFormat(int SampleRate, int ChannelCount, SampleFormat SampleFormat)
{
    /// <summary>
    /// Creates a float32 PCM format.
    /// </summary>
    public static AudioFormat CreateFloat32(int sampleRate, int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(channelCount, 0);
        return new AudioFormat(sampleRate, channelCount, SampleFormat.Float32);
    }

    /// <summary>Number of interleaved float samples that make up one second of audio.</summary>
    public int SamplesPerSecond => checked(SampleRate * ChannelCount);

    /// <summary>
    /// Converts a whole-frame count to a <see cref="TimeSpan"/> using integer tick arithmetic
    /// to avoid cumulative floating-point drift.
    /// Chosen so <see cref="TimeToFrames"/> round-trips: <c>TimeToFrames(FramesToTime(f)) == f</c>.
    /// </summary>
    public TimeSpan FramesToTime(long frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        if (SampleRate <= 0 || frames == 0)
        {
            return TimeSpan.Zero;
        }

        // Ceiling division so floor(TimeToFrames) recovers <paramref name="frames"/>.
        // ticks = ceil(frames * TicksPerSecond / SampleRate)
        var ticks = checked((frames * TimeSpan.TicksPerSecond + SampleRate - 1) / SampleRate);
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// Converts a time value to a whole-frame index using floor rounding:
    /// <c>frameIndex = floor(seconds × sampleRate)</c> via tick arithmetic.
    /// </summary>
    public long TimeToFrames(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }

        if (SampleRate <= 0 || time == TimeSpan.Zero)
        {
            return 0;
        }

        // frames = floor(ticks * SampleRate / TicksPerSecond)
        return checked(time.Ticks * SampleRate / TimeSpan.TicksPerSecond);
    }

    /// <summary>Converts an interleaved sample count (must be frame-aligned) to a <see cref="TimeSpan"/>.</summary>
    public TimeSpan SamplesToTime(long interleavedSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(interleavedSamples);
        if (ChannelCount <= 0)
        {
            return TimeSpan.Zero;
        }

        return FramesToTime(interleavedSamples / ChannelCount);
    }

    /// <summary>
    /// Converts a time value to a frame-aligned interleaved sample count
    /// (<c>TimeToFrames(time) × ChannelCount</c>).
    /// </summary>
    public long TimeToSamples(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }

        return checked(TimeToFrames(time) * ChannelCount);
    }

    /// <summary>Clamps a frame index to <c>[0, maxFrames]</c> when <paramref name="maxFrames"/> is known (≥ 0).</summary>
    public static long ClampFrameIndex(long frameIndex, long maxFrames)
    {
        if (frameIndex < 0)
        {
            return 0;
        }

        if (maxFrames >= 0 && frameIndex > maxFrames)
        {
            return maxFrames;
        }

        return frameIndex;
    }
}
