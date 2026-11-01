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

    /// <summary>Converts a sample count (interleaved floats) to a <see cref="TimeSpan"/>.</summary>
    public TimeSpan SamplesToTime(long interleavedSamples)
    {
        if (SamplesPerSecond <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(interleavedSamples / (double)SamplesPerSecond);
    }

    /// <summary>Converts a time value to an interleaved sample count.</summary>
    public long TimeToSamples(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }

        return (long)(time.TotalSeconds * SamplesPerSecond);
    }
}
