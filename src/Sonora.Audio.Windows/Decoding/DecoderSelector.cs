using NAudio.Vorbis;
using NAudio.Wave;
using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Sniffs local audio files and opens the appropriate <see cref="IAudioDecoder"/> backend.
/// </summary>
internal static class DecoderSelector
{
    // ASF header object GUID: 75B22630-668E-11CF-A6D9-00AA0062CE6C
    private static ReadOnlySpan<byte> AsfHeaderGuid =>
    [
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    ];

    private static ReadOnlySpan<byte> EbmlMagic => [0x1A, 0x45, 0xDF, 0xA3];

    public static IAudioDecoder Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Audio file was not found.", filePath);
        }

        try
        {
            var kind = Sniff(filePath);
            return kind switch
            {
                AudioContainerKind.Wave => WaveStreamAudioDecoder.OpenWave(filePath),
                AudioContainerKind.Aiff => WaveStreamAudioDecoder.OpenAiff(filePath),
                AudioContainerKind.OggVorbis => WaveStreamAudioDecoder.FromWaveStream(
                    new VorbisWaveReader(filePath),
                    sourcePath: null,
                    isMediaFoundation: false),
                AudioContainerKind.OggOpus => OpusOggAudioDecoder.Open(filePath),
                AudioContainerKind.Flac
                    or AudioContainerKind.MpegAudio
                    or AudioContainerKind.Mpeg4
                    or AudioContainerKind.AdtsAac
                    or AudioContainerKind.Asf
                    or AudioContainerKind.WebM
                    => WaveStreamAudioDecoder.OpenMediaFoundation(filePath),
                _ => throw new UnsupportedFormatException(
                    $"The audio format of '{filePath}' is not supported."),
            };
        }
        catch (Exception ex) when (ex is not AudioException and not FileNotFoundException and not OperationCanceledException)
        {
            throw MapOpenException(filePath, ex);
        }
    }

    public static AudioContainerKind Sniff(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        Span<byte> header = stackalloc byte[64];
        var read = stream.Read(header);
        if (read < 12)
        {
            return AudioContainerKind.Unknown;
        }

        var h = header[..read];

        if (HasAscii(h, 0, "RIFF") && read >= 12 && HasAscii(h, 8, "WAVE"))
        {
            return AudioContainerKind.Wave;
        }

        if (HasAscii(h, 0, "FORM") && read >= 12
            && (HasAscii(h, 8, "AIFF") || HasAscii(h, 8, "AIFC")))
        {
            return AudioContainerKind.Aiff;
        }

        if (HasAscii(h, 0, "fLaC"))
        {
            return AudioContainerKind.Flac;
        }

        if (read >= 16 && h[..16].SequenceEqual(AsfHeaderGuid))
        {
            return AudioContainerKind.Asf;
        }

        if (read >= 4 && h[..4].SequenceEqual(EbmlMagic))
        {
            return AudioContainerKind.WebM;
        }

        if (HasAscii(h, 0, "OggS"))
        {
            return SniffOgg(stream, h);
        }

        if (read >= 8 && HasAscii(h, 4, "ftyp"))
        {
            return AudioContainerKind.Mpeg4;
        }

        if (IsAdtsAac(h))
        {
            return AudioContainerKind.AdtsAac;
        }

        if (HasAscii(h, 0, "ID3") || IsMpegAudioFrame(h))
        {
            return AudioContainerKind.MpegAudio;
        }

        return AudioContainerKind.Unknown;
    }

    private static AudioContainerKind SniffOgg(Stream stream, ReadOnlySpan<byte> firstPage)
    {
        // Prefer scanning the first ~8 KiB for OpusHead / vorbis identification.
        stream.Position = 0;
        var buffer = new byte[Math.Min(8192, (int)Math.Max(stream.Length, firstPage.Length))];
        var n = stream.Read(buffer);
        var span = buffer.AsSpan(0, n);

        if (IndexOf(span, "OpusHead"u8) >= 0)
        {
            return AudioContainerKind.OggOpus;
        }

        if (IndexOf(span, "\x01vorbis"u8) >= 0 || IndexOf(span, "vorbis"u8) >= 0)
        {
            return AudioContainerKind.OggVorbis;
        }

        return AudioContainerKind.Unknown;
    }

    private static bool IsAdtsAac(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2)
        {
            return false;
        }

        // ADTS sync word 0xFFF, layer bits must be 0.
        if (header[0] != 0xFF || (header[1] & 0xF0) != 0xF0)
        {
            return false;
        }

        var layer = (header[1] >> 1) & 0x03;
        return layer == 0;
    }

    private static bool IsMpegAudioFrame(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2)
        {
            return false;
        }

        if (header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        var layer = (header[1] >> 1) & 0x03;
        // MPEG Audio layers I/II/III have non-zero layer bits; AAC ADTS uses 00.
        return layer != 0;
    }

    private static bool HasAscii(ReadOnlySpan<byte> data, int offset, string ascii)
    {
        if (data.Length < offset + ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (data[offset + i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private static Exception MapOpenException(string filePath, Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Contains("not support", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No audio", StringComparison.OrdinalIgnoreCase)
            || message.Contains("byte stream type is not supported", StringComparison.OrdinalIgnoreCase))
        {
            return new UnsupportedFormatException($"The audio format of '{filePath}' is not supported.", ex);
        }

        return new InvalidAudioException($"The audio file '{filePath}' could not be opened.", ex);
    }
}
