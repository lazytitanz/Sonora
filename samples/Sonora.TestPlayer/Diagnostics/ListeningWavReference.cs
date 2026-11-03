using System.Buffers.Binary;

namespace Sonora.TestPlayer.Diagnostics;

/// <summary>
/// Minimal independent PCM16 WAV reader for listening A/B integrity (no NAudio / Sonora decoder).
/// </summary>
internal static class ListeningWavReference
{
    private const ushort WaveFormatPcm = 0x0001;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    public sealed class Result
    {
        public required int SampleRate { get; init; }
        public required int Channels { get; init; }
        public required float[] Samples { get; init; }
        public required string FormatDescription { get; init; }
    }

    public static bool TryReadPcm16(string path, out Result? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            error = "Reference compare requires a .wav path.";
            return false;
        }

        try
        {
            result = Read(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static Result Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> four = stackalloc byte[4];
        ReadExact(stream, four);
        if (!Eq(four, "RIFF"))
        {
            throw new InvalidDataException("Not a RIFF WAVE file.");
        }

        _ = ReadU32(stream);
        ReadExact(stream, four);
        if (!Eq(four, "WAVE"))
        {
            throw new InvalidDataException("Missing WAVE form.");
        }

        ushort? tag = null;
        int rate = 0, ch = 0, bits = 0, blockAlign = 0;
        var extensible = false;
        Guid? sub = null;
        long dataOffset = -1, dataLen = -1;

        while (stream.Position + 8 <= stream.Length)
        {
            ReadExact(stream, four);
            var size = ReadU32(stream);
            var start = stream.Position;
            if (Eq(four, "fmt "))
            {
                var fmt = new byte[size];
                ReadExact(stream, fmt);
                tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                ch = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12, 2));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));
                if (tag == WaveFormatExtensible)
                {
                    extensible = true;
                    if (size < 40)
                    {
                        throw new InvalidDataException("WAVE_FORMAT_EXTENSIBLE fmt truncated.");
                    }

                    sub = new Guid(fmt.AsSpan(24, 16));
                }
            }
            else if (Eq(four, "data"))
            {
                dataOffset = start;
                dataLen = size;
                stream.Position = start + size + (size & 1);
                if (tag.HasValue)
                {
                    break;
                }

                continue;
            }
            else
            {
                stream.Position = start + size + (size & 1);
                continue;
            }

            stream.Position = start + size + (size & 1);
        }

        if (!tag.HasValue || dataOffset < 0)
        {
            throw new InvalidDataException("Missing fmt or data chunk.");
        }

        if (tag == WaveFormatExtensible)
        {
            if (sub != SubtypePcm)
            {
                throw new NotSupportedException($"WAVE_FORMAT_EXTENSIBLE SubFormat {sub} is not PCM.");
            }
        }
        else if (tag != WaveFormatPcm)
        {
            throw new NotSupportedException($"Unsupported WAV format tag 0x{tag:X4}.");
        }

        if (bits != 16)
        {
            throw new NotSupportedException($"BitsPerSample={bits}; need PCM16.");
        }

        var count = checked((int)(dataLen / 2));
        var floats = new float[count];
        stream.Position = dataOffset;
        var raw = new byte[checked((int)dataLen)];
        ReadExact(stream, raw);
        for (var i = 0; i < count; i++)
        {
            floats[i] = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i * 2, 2)) / 32768f;
        }

        return new Result
        {
            SampleRate = rate,
            Channels = ch,
            Samples = floats,
            FormatDescription = extensible
                ? $"WAVE_FORMAT_EXTENSIBLE PCM16 {rate}Hz {ch}ch"
                : $"WAVE_FORMAT_PCM16 {rate}Hz {ch}ch",
        };
    }

    public static float[] Slice(float[] samples, int sampleRate, int channels, TimeSpan start, TimeSpan duration)
    {
        channels = Math.Max(1, channels);
        var startFrame = Math.Max(0, (int)Math.Floor(start.TotalSeconds * sampleRate));
        var frames = Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds * sampleRate));
        var startSample = startFrame * channels;
        if (startSample >= samples.Length)
        {
            return Array.Empty<float>();
        }

        var count = Math.Min(frames * channels, samples.Length - startSample);
        count -= count % channels;
        return count <= 0 ? Array.Empty<float>() : samples.AsSpan(startSample, count).ToArray();
    }

    private static bool Eq(ReadOnlySpan<byte> id, string s) =>
        id.Length == 4 && id[0] == (byte)s[0] && id[1] == (byte)s[1] && id[2] == (byte)s[2] && id[3] == (byte)s[3];

    private static uint ReadU32(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        ReadExact(s, b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static void ReadExact(Stream s, Span<byte> dest)
    {
        var o = 0;
        while (o < dest.Length)
        {
            var n = s.Read(dest[o..]);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            o += n;
        }
    }
}
