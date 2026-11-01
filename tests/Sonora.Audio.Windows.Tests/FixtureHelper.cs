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
        => EnsureEncodedOrCommitted(
            $"tone-{duration.TotalMilliseconds:0}ms.mp3",
            "tone-1s.mp3",
            duration,
            path =>
            {
                var wav = EnsureToneWav(duration);
                using var reader = new AudioFileReader(wav);
                MediaFoundationEncoder.EncodeToMp3(reader, path, 128000);
            });

    public static string EnsureToneFlac(TimeSpan duration)
        => EnsureEncodedOrCommitted(
            $"tone-{duration.TotalMilliseconds:0}ms.flac",
            "tone-1s.flac",
            duration,
            path =>
            {
                var wav = EnsureToneWav(duration);
                using var reader = new AudioFileReader(wav);
                MediaFoundationEncoder.EncodeToFlac(reader, path);
            });

    public static string EnsureToneM4a(TimeSpan duration)
        => EnsureEncodedOrCommitted(
            $"tone-{duration.TotalMilliseconds:0}ms.m4a",
            "tone-1s.m4a",
            duration,
            path =>
            {
                var wav = EnsureToneWav(duration);
                using var reader = new AudioFileReader(wav);
                MediaFoundationEncoder.EncodeToAac(reader, path, 128000);
            });

    public static string EnsureToneAiff(TimeSpan duration)
        => EnsureEncodedOrCommitted(
            $"tone-{duration.TotalMilliseconds:0}ms.aiff",
            "tone-1s.aiff",
            duration,
            path =>
            {
                var wav = EnsureToneWav(duration);
                using var reader = new AudioFileReader(wav);
                AiffFileWriter.CreateAiffFile(path, reader);
            });

    public static string EnsureToneWma(TimeSpan duration)
        => EnsureEncodedOrCommitted(
            $"tone-{duration.TotalMilliseconds:0}ms.wma",
            "tone-1s.wma",
            duration,
            path =>
            {
                var wav = EnsureToneWav(duration);
                using var reader = new AudioFileReader(wav);
                MediaFoundationEncoder.EncodeToWma(reader, path, 128000);
            });

    public static string EnsureToneAdtsAac(TimeSpan duration)
        => EnsureEncodedOrCommitted(
            $"tone-{duration.TotalMilliseconds:0}ms.aac",
            "tone-1s.aac",
            duration,
            path =>
            {
                var wav = EnsureToneWav(duration);
                using var reader = new AudioFileReader(wav);
                MediaFoundationEncoder.EncodeToAac(reader, path, 128000);
            });

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

    /// <summary>
    /// Segment frequencies used by the deterministic multi-tone FLAC fixture.
    /// </summary>
    public static readonly float[] SegmentFrequenciesHz = [220f, 440f, 880f, 1760f];

    /// <summary>
    /// Creates a FLAC file with four equal-length pure-tone segments (220/440/880/1760 Hz).
    /// Falls back to the committed fixture when Media Foundation FLAC encoding is unavailable.
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

            try
            {
                var wavPath = Path.Combine(FixturesDirectory, $"segments-{segmentMs}ms.wav");
                WriteSegmentedToneWav(wavPath, TimeSpan.FromMilliseconds(segmentMs), sampleRate, channels, SegmentFrequenciesHz);
                using var reader = new AudioFileReader(wavPath);
                MediaFoundationEncoder.EncodeToFlac(reader, path);
                return path;
            }
            catch (Exception) when (segmentMs == 250)
            {
                return EnsureCommittedFixture("segments-250ms.flac");
            }
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

    private static string EnsureEncodedOrCommitted(
        string generatedFileName,
        string committedFileName,
        TimeSpan requestedDuration,
        Action<string> encode)
    {
        var path = Path.Combine(FixturesDirectory, generatedFileName);
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                encode(path);
                return path;
            }
            catch (Exception) when (requestedDuration <= TimeSpan.FromSeconds(1.05))
            {
                // GitHub-hosted Windows runners often lack MF encoders; use checked-in tones.
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return EnsureCommittedFixture(committedFileName);
            }
        }
    }

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
