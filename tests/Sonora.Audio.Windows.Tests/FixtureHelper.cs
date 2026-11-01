using System.Buffers.Binary;
using NAudio.Wave;

namespace Sonora.Audio.Windows.Tests;

internal static class FixtureHelper
{
    private static readonly object Gate = new();

    public static string FixturesDirectory
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string EnsureToneWav(TimeSpan duration, int sampleRate = 44100, int channels = 2)
    {
        var name = $"tone-{duration.TotalMilliseconds:0}ms.wav";
        var path = Path.Combine(FixturesDirectory, name);
        lock (Gate)
        {
            if (!File.Exists(path))
            {
                WriteToneWav(path, duration, sampleRate, channels);
            }
        }

        return path;
    }

    public static string EnsureToneMp3(TimeSpan duration)
    {
        var path = Path.Combine(FixturesDirectory, $"tone-{duration.TotalMilliseconds:0}ms.mp3");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return path;
            }

            var wav = EnsureToneWav(duration);
            using var reader = new AudioFileReader(wav);
            MediaFoundationEncoder.EncodeToMp3(reader, path, 128000);
            return path;
        }
    }

    public static string EnsureToneFlac(TimeSpan duration)
    {
        var path = Path.Combine(FixturesDirectory, $"tone-{duration.TotalMilliseconds:0}ms.flac");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return path;
            }

            var wav = EnsureToneWav(duration);
            using var reader = new AudioFileReader(wav);
            MediaFoundationEncoder.EncodeToFlac(reader, path);
            return path;
        }
    }

    public static string EnsureToneM4a(TimeSpan duration)
    {
        var path = Path.Combine(FixturesDirectory, $"tone-{duration.TotalMilliseconds:0}ms.m4a");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return path;
            }

            var wav = EnsureToneWav(duration);
            using var reader = new AudioFileReader(wav);
            // AAC in an MPEG-4 container (.m4a) via Media Foundation.
            MediaFoundationEncoder.EncodeToAac(reader, path, 128000);
            return path;
        }
    }

    public static string EnsureToneAiff(TimeSpan duration)
    {
        var path = Path.Combine(FixturesDirectory, $"tone-{duration.TotalMilliseconds:0}ms.aiff");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return path;
            }

            var wav = EnsureToneWav(duration);
            using var reader = new AudioFileReader(wav);
            AiffFileWriter.CreateAiffFile(path, reader);
            return path;
        }
    }

    public static string EnsureToneWma(TimeSpan duration)
    {
        var path = Path.Combine(FixturesDirectory, $"tone-{duration.TotalMilliseconds:0}ms.wma");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return path;
            }

            var wav = EnsureToneWav(duration);
            using var reader = new AudioFileReader(wav);
            MediaFoundationEncoder.EncodeToWma(reader, path, 128000);
            return path;
        }
    }

    public static string EnsureToneAdtsAac(TimeSpan duration)
    {
        var path = Path.Combine(FixturesDirectory, $"tone-{duration.TotalMilliseconds:0}ms.aac");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return path;
            }

            var wav = EnsureToneWav(duration);
            using var reader = new AudioFileReader(wav);
            // NAudio MF AAC encoder writes ADTS when the destination extension is .aac.
            MediaFoundationEncoder.EncodeToAac(reader, path, 128000);
            return path;
        }
    }

    /// <summary>
    /// ALAC-in-M4A fixture. Uses a committed sample (MF ALAC encode is not reliably available via NAudio).
    /// </summary>
    public static string EnsureToneAlacM4a()
        => EnsureCommittedFixture("tone-1s-alac.m4a");

    public static string EnsureToneOggVorbis()
        => EnsureCommittedFixture("tone-1s.ogg");

    public static string EnsureToneOpus()
        => EnsureCommittedFixture("tone-1s.opus");

    public static string EnsureToneWebmOpus()
        => EnsureCommittedFixture("tone-1s-opus.webm");

    public static string EnsureToneWebmVorbis()
        => EnsureCommittedFixture("tone-1s-vorbis.webm");

    private static string EnsureCommittedFixture(string fileName)
    {
        var dest = Path.Combine(FixturesDirectory, fileName);
        lock (Gate)
        {
            if (File.Exists(dest))
            {
                return dest;
            }

            var source = Path.Combine(AppContext.BaseDirectory, "CommittedFixtures", fileName);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    $"Committed fixture '{fileName}' was not found next to the test assembly.", source);
            }

            File.Copy(source, dest, overwrite: true);
            return dest;
        }
    }

    /// <summary>
    /// Segment frequencies used by the deterministic multi-tone FLAC fixture.
    /// </summary>
    public static readonly float[] SegmentFrequenciesHz = [220f, 440f, 880f, 1760f];

    /// <summary>
    /// Creates a FLAC file with four equal-length pure-tone segments (220/440/880/1760 Hz).
    /// </summary>
    public static string EnsureSegmentedFlac(int segmentMs = 250, int sampleRate = 44100, int channels = 2)
    {
        var path = Path.Combine(FixturesDirectory, $"segments-{segmentMs}ms.flac");
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return path;
            }

            var wavPath = Path.Combine(FixturesDirectory, $"segments-{segmentMs}ms.wav");
            WriteSegmentedToneWav(wavPath, TimeSpan.FromMilliseconds(segmentMs), sampleRate, channels, SegmentFrequenciesHz);
            using var reader = new AudioFileReader(wavPath);
            MediaFoundationEncoder.EncodeToFlac(reader, path);
            return path;
        }
    }

    public static TimeSpan SegmentedDuration(int segmentMs = 250)
        => TimeSpan.FromMilliseconds(segmentMs * SegmentFrequenciesHz.Length);

    public static TimeSpan SegmentCenter(int segmentIndex, int segmentMs = 250)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(segmentIndex, SegmentFrequenciesHz.Length);
        return TimeSpan.FromMilliseconds(segmentMs * segmentIndex + segmentMs / 2.0);
    }

    public static void WriteToneWav(string path, TimeSpan duration, int sampleRate, int channels, float frequencyHz = 440f)
    {
        WriteSegmentedToneWav(path, duration, sampleRate, channels, [frequencyHz]);
    }

    public static void WriteSegmentedToneWav(
        string path,
        TimeSpan segmentDuration,
        int sampleRate,
        int channels,
        IReadOnlyList<float> frequenciesHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequenciesHz.Count, 0);
        var framesPerSegment = (int)(sampleRate * segmentDuration.TotalSeconds);
        var totalFrames = framesPerSegment * frequenciesHz.Count;
        var dataLength = totalFrames * channels * 2; // 16-bit
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        Span<byte> header = stackalloc byte[44];
        WriteWavHeader(header, sampleRate, channels, dataLength);
        stream.Write(header);

        var frame = new byte[channels * 2];
        for (var segment = 0; segment < frequenciesHz.Count; segment++)
        {
            var frequencyHz = frequenciesHz[segment];
            for (var i = 0; i < framesPerSegment; i++)
            {
                var t = i / (double)sampleRate;
                var sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * t) * short.MaxValue * 0.2);
                for (var ch = 0; ch < channels; ch++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(ch * 2, 2), sample);
                }

                stream.Write(frame);
            }
        }
    }

    private static void WriteWavHeader(Span<byte> header, int sampleRate, int channels, int dataLength)
    {
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataLength);
        System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(header[8..]);
        System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        var byteRate = sampleRate * channels * 2;
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataLength);
    }
}
