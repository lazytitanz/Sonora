using System.Buffers.Binary;
using System.Text;

namespace Sonora.Audio.Windows.Tests.Diagnostics;

/// <summary>
/// Minimal independent PCM16 WAV reader for integrity diagnostics.
/// Does not use NAudio or Sonora decoders.
/// </summary>
internal static class IndependentWavPcm16Reader
{
    private const ushort WaveFormatPcm = 0x0001;
    private const ushort WaveFormatExtensible = 0xFFFE;

    /// <summary>KSDATAFORMAT_SUBTYPE_PCM — {00000001-0000-0010-8000-00AA00389B71}</summary>
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    public sealed class Result
    {
        public required int SampleRate { get; init; }
        public required int Channels { get; init; }
        public required int BitsPerSample { get; init; }
        public required int BlockAlign { get; init; }
        public required int ByteRate { get; init; }
        public required long DataByteLength { get; init; }
        public required bool IsExtensible { get; init; }
        public required float[] Samples { get; init; }
        public required string FormatDescription { get; init; }

        public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;

        public TimeSpan Duration =>
            SampleRate > 0 && Channels > 0
                ? TimeSpan.FromSeconds(FrameCount / (double)SampleRate)
                : TimeSpan.Zero;
    }

    public static Result Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream, path);
    }

    public static Result Read(Stream stream, string? pathForErrors = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new NotSupportedException("Independent WAV reader requires a seekable stream.");
        }

        Span<byte> four = stackalloc byte[4];
        ReadExact(stream, four);
        if (!ChunkIdEquals(four, "RIFF"))
        {
            throw new InvalidDataException(Prefix(pathForErrors) + "Not a RIFF file (missing RIFF chunk).");
        }

        _ = ReadUInt32(stream); // riff size
        ReadExact(stream, four);
        if (!ChunkIdEquals(four, "WAVE"))
        {
            throw new InvalidDataException(Prefix(pathForErrors) + "Not a WAVE file (missing WAVE form type).");
        }

        ushort? formatTag = null;
        int sampleRate = 0;
        int channels = 0;
        int bitsPerSample = 0;
        int blockAlign = 0;
        int byteRate = 0;
        var isExtensible = false;
        Guid? subFormat = null;
        long dataOffset = -1;
        long dataByteLength = -1;

        while (stream.Position + 8 <= stream.Length)
        {
            ReadExact(stream, four);
            var chunkSize = ReadUInt32(stream);
            var chunkStart = stream.Position;

            if (ChunkIdEquals(four, "fmt "))
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException(Prefix(pathForErrors) + $"fmt chunk too small ({chunkSize} bytes).");
                }

                var fmt = new byte[chunkSize];
                ReadExact(stream, fmt);
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(8, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));

                if (formatTag == WaveFormatExtensible)
                {
                    isExtensible = true;
                    if (chunkSize < 40)
                    {
                        throw new InvalidDataException(
                            Prefix(pathForErrors)
                            + "WAVE_FORMAT_EXTENSIBLE fmt chunk is truncated (need at least 40 bytes).");
                    }

                    // cbSize at 16, valid bits 18, channel mask 20, subFormat 24..39
                    subFormat = new Guid(fmt.AsSpan(24, 16));
                }
            }
            else if (ChunkIdEquals(four, "data"))
            {
                dataOffset = chunkStart;
                dataByteLength = chunkSize;
                stream.Position = chunkStart + chunkSize + (chunkSize & 1);
                if (formatTag.HasValue)
                {
                    break;
                }

                continue;
            }
            else
            {
                // Skip ordinary metadata / padding chunks (LIST, fact, JUNK, bext, iXML, etc.).
                stream.Position = chunkStart + chunkSize + (chunkSize & 1);
                continue;
            }

            stream.Position = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (!formatTag.HasValue)
        {
            throw new InvalidDataException(Prefix(pathForErrors) + "Missing fmt chunk.");
        }

        if (dataOffset < 0 || dataByteLength < 0)
        {
            throw new InvalidDataException(Prefix(pathForErrors) + "Missing data chunk.");
        }

        ValidateFormat(
            pathForErrors,
            formatTag.Value,
            isExtensible,
            subFormat,
            sampleRate,
            channels,
            bitsPerSample,
            blockAlign,
            byteRate);

        var expectedBlockAlign = channels * (bitsPerSample / 8);
        if (blockAlign != expectedBlockAlign)
        {
            throw new InvalidDataException(
                Prefix(pathForErrors)
                + $"BlockAlign {blockAlign} does not match channels×bytesPerSample ({expectedBlockAlign}).");
        }

        if (dataByteLength % blockAlign != 0)
        {
            throw new InvalidDataException(
                Prefix(pathForErrors)
                + $"Data length {dataByteLength} is not divisible by BlockAlign {blockAlign}.");
        }

        var sampleCount = checked((int)(dataByteLength / sizeof(short)));
        var floats = new float[sampleCount];
        stream.Position = dataOffset;
        var raw = new byte[checked((int)dataByteLength)];
        ReadExact(stream, raw);
        for (var i = 0; i < sampleCount; i++)
        {
            var pcm = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i * 2, 2));
            // Match NAudio WaveToSampleProvider PCM16 scaling used by WaveStreamAudioDecoder.
            floats[i] = pcm / 32768f;
        }

        var desc = isExtensible
            ? $"WAVE_FORMAT_EXTENSIBLE PCM16 LE {sampleRate}Hz {channels}ch blockAlign={blockAlign}"
            : $"WAVE_FORMAT_PCM16 LE {sampleRate}Hz {channels}ch blockAlign={blockAlign}";

        return new Result
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            BlockAlign = blockAlign,
            ByteRate = byteRate,
            DataByteLength = dataByteLength,
            IsExtensible = isExtensible,
            Samples = floats,
            FormatDescription = desc,
        };
    }

    private static void ValidateFormat(
        string? pathForErrors,
        ushort formatTag,
        bool isExtensible,
        Guid? subFormat,
        int sampleRate,
        int channels,
        int bitsPerSample,
        int blockAlign,
        int byteRate)
    {
        if (formatTag == WaveFormatPcm)
        {
            // classic PCM
        }
        else if (formatTag == WaveFormatExtensible)
        {
            if (subFormat != SubtypePcm)
            {
                throw new NotSupportedException(
                    Prefix(pathForErrors)
                    + "WAVE_FORMAT_EXTENSIBLE is present but SubFormat is not PCM "
                    + $"(got {subFormat}). Independent reference reader only supports PCM16.");
            }
        }
        else
        {
            throw new NotSupportedException(
                Prefix(pathForErrors)
                + $"Unsupported WAV format tag 0x{formatTag:X4}. "
                + "Independent reference reader supports WAVE_FORMAT_PCM (1) and "
                + "WAVE_FORMAT_EXTENSIBLE with PCM SubFormat only.");
        }

        if (bitsPerSample != 16)
        {
            throw new NotSupportedException(
                Prefix(pathForErrors)
                + $"BitsPerSample={bitsPerSample}; independent reference reader requires PCM16.");
        }

        if (sampleRate <= 0 || channels <= 0 || blockAlign <= 0)
        {
            throw new InvalidDataException(
                Prefix(pathForErrors)
                + $"Invalid fmt fields: rate={sampleRate} ch={channels} blockAlign={blockAlign} byteRate={byteRate}.");
        }

        _ = isExtensible;
    }

    private static string Prefix(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : $"{path}: ";

    private static bool ChunkIdEquals(ReadOnlySpan<byte> id, string ascii4) =>
        id.Length == 4
        && id[0] == (byte)ascii4[0]
        && id[1] == (byte)ascii4[1]
        && id[2] == (byte)ascii4[2]
        && id[3] == (byte)ascii4[3];

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> b = stackalloc byte[4];
        ReadExact(stream, b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static void ReadExact(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var n = stream.Read(destination[offset..]);
            if (n == 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected EOF reading WAV (wanted {destination.Length}, got {offset}).");
            }

            offset += n;
        }
    }

    /// <summary>Writes a tiny PCM16 WAV that includes a LIST/INFO chunk before data (for reader tests).</summary>
    public static void WritePcm16WithListChunk(
        string path,
        int sampleRate,
        int channels,
        ReadOnlySpan<short> interleavedPcm)
    {
        var dataBytes = interleavedPcm.Length * sizeof(short);
        var blockAlign = (ushort)(channels * 2);
        var byteRate = sampleRate * blockAlign;

        var inamText = Encoding.ASCII.GetBytes("diag\0");
        var inamSize = inamText.Length;
        if ((inamSize & 1) != 0)
        {
            inamSize++;
        }

        var listChunkSize = 4 + 8 + inamSize; // "INFO" + INAM hdr + text(+pad)

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        Span<byte> hdr = stackalloc byte[12];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(hdr);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(hdr[8..]);
        fs.Write(hdr);

        WriteChunkId(fs, "fmt ");
        WriteUInt32(fs, 16);
        WriteUInt16(fs, WaveFormatPcm);
        WriteUInt16(fs, (ushort)channels);
        WriteInt32(fs, sampleRate);
        WriteInt32(fs, byteRate);
        WriteUInt16(fs, blockAlign);
        WriteUInt16(fs, 16);

        WriteChunkId(fs, "LIST");
        WriteUInt32(fs, (uint)listChunkSize);
        WriteChunkId(fs, "INFO");
        WriteChunkId(fs, "INAM");
        WriteUInt32(fs, (uint)inamText.Length);
        fs.Write(inamText);
        if ((inamText.Length & 1) != 0)
        {
            fs.WriteByte(0);
        }

        WriteChunkId(fs, "data");
        WriteUInt32(fs, (uint)dataBytes);
        Span<byte> sampleBuf = stackalloc byte[2];
        for (var i = 0; i < interleavedPcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(sampleBuf, interleavedPcm[i]);
            fs.Write(sampleBuf);
        }

        var riffSize = (uint)(fs.Length - 8);
        fs.Position = 4;
        WriteUInt32(fs, riffSize);
    }

    /// <summary>Writes WAVE_FORMAT_EXTENSIBLE PCM16 for reader coverage.</summary>
    public static void WriteExtensiblePcm16(
        string path,
        int sampleRate,
        int channels,
        ReadOnlySpan<short> interleavedPcm,
        Guid? subFormatOverride = null)
    {
        var dataBytes = interleavedPcm.Length * sizeof(short);
        var blockAlign = (ushort)(channels * 2);
        var byteRate = sampleRate * blockAlign;
        var sub = subFormatOverride ?? SubtypePcm;
        uint channelMask = channels switch
        {
            1 => 0x4, // SPEAKER_FRONT_CENTER
            2 => 0x3, // FL|FR
            _ => 0,
        };

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        Span<byte> hdr = stackalloc byte[12];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(hdr);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(hdr[8..]);
        fs.Write(hdr);

        WriteChunkId(fs, "fmt ");
        WriteUInt32(fs, 40);
        WriteUInt16(fs, WaveFormatExtensible);
        WriteUInt16(fs, (ushort)channels);
        WriteInt32(fs, sampleRate);
        WriteInt32(fs, byteRate);
        WriteUInt16(fs, blockAlign);
        WriteUInt16(fs, 16);
        WriteUInt16(fs, 22); // cbSize
        WriteUInt16(fs, 16); // valid bits
        WriteUInt32(fs, channelMask);
        Span<byte> guidBytes = stackalloc byte[16];
        sub.TryWriteBytes(guidBytes);
        fs.Write(guidBytes);

        WriteChunkId(fs, "data");
        WriteUInt32(fs, (uint)dataBytes);
        Span<byte> sampleBuf = stackalloc byte[2];
        for (var i = 0; i < interleavedPcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(sampleBuf, interleavedPcm[i]);
            fs.Write(sampleBuf);
        }

        var riffSize = (uint)(fs.Length - 8);
        fs.Position = 4;
        WriteUInt32(fs, riffSize);
    }

    private static void WriteChunkId(Stream s, string id)
    {
        Span<byte> b = stackalloc byte[4];
        Encoding.ASCII.GetBytes(id).CopyTo(b);
        s.Write(b);
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteInt32(Stream s, int v) => WriteUInt32(s, unchecked((uint)v));

    private static void WriteUInt16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }
}
