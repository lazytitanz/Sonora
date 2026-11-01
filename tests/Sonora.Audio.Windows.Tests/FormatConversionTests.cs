using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;
using Sonora.Audio.Windows.Decoding;

namespace Sonora.Audio.Windows.Tests;

public sealed class FormatConversionTests
{
    [Fact]
    public void WdlConverter_ResamplesAndPreservesEnergy()
    {
        var sourceFormat = AudioFormat.CreateFloat32(44100, 1);
        var targetFormat = AudioFormat.CreateFloat32(48000, 1);
        var frames = 4410; // 100 ms
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            samples[i] = 0.25f * MathF.Sin(2 * MathF.PI * 440f * i / 44100f);
        }

        using var source = new SequenceWaveDecoder(sourceFormat, samples);
        var converter = new WindowsAudioFormatConverter();
        using var converted = converter.Convert(source, targetFormat);

        Assert.Equal(48000, converted.Format.SampleRate);
        Assert.Equal(1, converted.Format.ChannelCount);

        var buffer = new float[8192];
        var total = 0;
        double energy = 0;
        int read;
        while ((read = converted.Read(buffer)) > 0)
        {
            total += read;
            for (var i = 0; i < read; i++)
            {
                energy += buffer[i] * buffer[i];
            }
        }

        // ~100 ms at 48 kHz
        Assert.InRange(total, 4000, 5600);
        Assert.True(energy > 1.0);
    }

    [Fact]
    public void WdlConverter_Identity_ReturnsSameDecoder()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        using var source = new SequenceWaveDecoder(format, new float[64]);
        var converter = new WindowsAudioFormatConverter();
        Assert.Same(source, converter.Convert(source, format));
    }

    private sealed class SequenceWaveDecoder : Sonora.Audio.Abstractions.IAudioDecoder
    {
        private readonly float[] _samples;
        private int _position;

        public SequenceWaveDecoder(AudioFormat format, float[] samples)
        {
            Format = format;
            _samples = samples;
            Duration = format.SamplesToTime(samples.Length);
        }

        public AudioFormat Format { get; }
        public TimeSpan Duration { get; }
        public bool HasKnownDuration => true;
        public bool CanSeek => true;

        public int Read(Span<float> destination)
        {
            var remaining = _samples.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var n = Math.Min(remaining, destination.Length);
            _samples.AsSpan(_position, n).CopyTo(destination);
            _position += n;
            return n;
        }

        public AudioSeekResult Seek(TimeSpan position)
        {
            var frames = Format.TimeToFrames(position);
            _position = (int)Math.Min(frames * Format.ChannelCount, _samples.Length);
            var requested = Format.FramesToTime(frames);
            return AudioSeekResult.Exact(requested, requested);
        }

        public void Dispose()
        {
        }
    }
}
