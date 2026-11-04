using NAudio.Vorbis;
using NAudio.Wave;
using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Sniffs local audio files and streams, then opens the appropriate <see cref="IAudioDecoder"/> backend.
/// </summary>
internal static class DecoderSelector
{
    private const int SniffBufferSize = 8192;

    /// <summary>Open-time empty-PCM probe threshold: shorter known durations may be legitimate empty.</summary>
    private static readonly TimeSpan SubstantialDurationThreshold = TimeSpan.FromMilliseconds(250);

    /// <summary>Interleaved float samples read during the open-time PCM probe.</summary>
    private const int OpenProbeSamples = 4096;

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
            return OpenKind(kind, filePath);
        }
        catch (Exception ex) when (ex is not AudioException and not FileNotFoundException and not OperationCanceledException)
        {
            throw MapOpenException(filePath, ex);
        }
    }

    /// <summary>
    /// Opens a decoder for an audio byte stream. The returned decoder owns <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Readable audio bytes. Prefer seekable streams for MF formats and seeking.</param>
    /// <param name="originName">Optional file name or URI used for diagnostics and MF origin hints.</param>
    /// <param name="contentTypeHint">Optional MIME type used when magic-byte sniffing is inconclusive.</param>
    public static IAudioDecoder Open(Stream stream, string? originName = null, string? contentTypeHint = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        var describe = string.IsNullOrWhiteSpace(originName) ? "stream" : originName;
        PeekBufferStream? peek = null;
        Stream openStream = stream;
        try
        {
            AudioContainerKind kind;
            if (stream.CanSeek)
            {
                var start = stream.Position;
                kind = Sniff(stream);
                stream.Position = start;
            }
            else
            {
                peek = PeekBufferStream.Create(stream, SniffBufferSize, leaveOpen: false);
                openStream = peek;
                kind = SniffHeader(peek.PrefetchSpan, peek);
                peek.RewindToStart();
            }

            if (kind == AudioContainerKind.Unknown)
            {
                kind = GuessFromContentType(contentTypeHint)
                    ?? GuessFromOriginName(originName)
                    ?? AudioContainerKind.Unknown;
            }

            return OpenKind(kind, openStream, describe, contentTypeHint);
        }
        catch (Exception ex) when (ex is not AudioException and not OperationCanceledException and not NotSupportedException)
        {
            peek?.Dispose();
            if (peek is null)
            {
                stream.Dispose();
            }

            throw MapOpenException(describe, ex);
        }
        catch
        {
            peek?.Dispose();
            if (peek is null)
            {
                // Ownership transfers only on success; dispose the caller's stream on failure.
                stream.Dispose();
            }

            throw;
        }
    }

    public static AudioContainerKind Sniff(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Sniff(stream);
    }

    /// <summary>
    /// Sniffs container kind from a stream. When seekable, position is restored.
    /// Non-seekable streams should be wrapped with <see cref="PeekBufferStream"/> first.
    /// </summary>
    public static AudioContainerKind Sniff(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream is PeekBufferStream peek)
        {
            return SniffHeader(peek.PrefetchSpan, peek);
        }

        if (!stream.CanSeek)
        {
            Span<byte> header = stackalloc byte[64];
            var read = stream.Read(header);
            return SniffHeader(header[..read], sourceForOgg: null);
        }

        var start = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[64];
            var read = stream.Read(header);
            if (read < 12)
            {
                return AudioContainerKind.Unknown;
            }

            return SniffHeader(header[..read], stream);
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static IAudioDecoder OpenKind(AudioContainerKind kind, string filePath)
        => kind switch
        {
            AudioContainerKind.Wave => WaveStreamAudioDecoder.OpenWave(filePath),
            AudioContainerKind.Aiff => WaveStreamAudioDecoder.OpenAiff(filePath),
            AudioContainerKind.OggVorbis => WaveStreamAudioDecoder.FromWaveStream(
                new VorbisWaveReader(filePath),
                sourcePath: filePath,
                isMediaFoundation: false,
                hasKnownDuration: true),
            AudioContainerKind.OggOpus => OpusOggAudioDecoder.Open(filePath),
            AudioContainerKind.FragmentedMpeg4 => OpenManagedMpeg4OrThrow(filePath),
            AudioContainerKind.Mpeg4 => OpenMpeg4WithFallback(filePath),
            AudioContainerKind.WebM => OpenWebMWithFallback(filePath),
            AudioContainerKind.Flac
                or AudioContainerKind.MpegAudio
                or AudioContainerKind.AdtsAac
                or AudioContainerKind.Asf
                => WaveStreamAudioDecoder.OpenMediaFoundation(filePath),
            _ => throw new UnsupportedFormatException(
                $"The audio format of '{filePath}' is not supported."),
        };

    private static IAudioDecoder OpenKind(
        AudioContainerKind kind,
        Stream stream,
        string originName,
        string? contentTypeHint)
        => kind switch
        {
            AudioContainerKind.Wave => WaveStreamAudioDecoder.OpenWave(stream),
            AudioContainerKind.Aiff => WaveStreamAudioDecoder.OpenAiff(stream),
            AudioContainerKind.OggVorbis => WaveStreamAudioDecoder.FromWaveStream(
                new VorbisWaveReader(stream, closeOnDispose: false),
                sourcePath: originName,
                isMediaFoundation: false,
                supportsExactByteSeek: false,
                hasKnownDuration: null,
                ownedByteStream: stream),
            AudioContainerKind.OggOpus => OpusOggAudioDecoder.Open(stream, originName, leaveOpen: false),
            AudioContainerKind.FragmentedMpeg4 => OpenManagedMpeg4OrThrow(stream, originName),
            AudioContainerKind.Mpeg4 => OpenMpeg4WithFallback(stream, originName, contentTypeHint),
            AudioContainerKind.WebM => OpenWebMWithFallback(stream, originName, contentTypeHint),
            AudioContainerKind.Flac
                or AudioContainerKind.MpegAudio
                or AudioContainerKind.AdtsAac
                or AudioContainerKind.Asf
                => WaveStreamAudioDecoder.OpenMediaFoundation(stream, originName, contentTypeHint),
            _ => throw new UnsupportedFormatException(
                $"The audio format of '{originName}' is not supported."),
        };

    private static IAudioDecoder OpenManagedMpeg4OrThrow(string filePath)
    {
        var decoder = ManagedAacMp4AudioDecoder.Open(filePath);
        return EnsureProducesPcmOrThrow(decoder, filePath);
    }

    private static IAudioDecoder OpenManagedMpeg4OrThrow(Stream stream, string originName)
    {
        var decoder = ManagedAacMp4AudioDecoder.Open(stream, originName, leaveOpen: false);
        return EnsureProducesPcmOrThrow(decoder, originName);
    }

    private static IAudioDecoder OpenManagedWebmOpusOrThrow(string filePath)
    {
        var decoder = ManagedWebmOpusAudioDecoder.Open(filePath);
        return EnsureProducesPcmOrThrow(decoder, filePath);
    }

    private static IAudioDecoder OpenManagedWebmOpusOrThrow(Stream stream, string originName)
    {
        var decoder = ManagedWebmOpusAudioDecoder.Open(stream, originName, leaveOpen: false);
        return EnsureProducesPcmOrThrow(decoder, originName);
    }

    private static IAudioDecoder OpenWebMWithFallback(string filePath)
    {
        IAudioDecoder? mf = null;
        try
        {
            mf = WaveStreamAudioDecoder.OpenMediaFoundation(filePath);
            var probed = TryProbePcm(mf, out var leftover, out var leftoverLength);
            if (probed > 0)
            {
                return leftoverLength > 0
                    ? new PrefixedAudioDecoder(mf, leftover!, leftoverLength)
                    : mf;
            }

            if (!IsSubstantialKnownDuration(mf))
            {
                return mf;
            }

            mf.Dispose();
            mf = null;
            return OpenManagedWebmOpusOrThrow(filePath);
        }
        catch (Exception ex) when (mf is null && IsMediaFoundationOpenFailure(ex))
        {
            // MF often rejects real-world WebM Opus (MF_E_INVALID_FORMAT) that Concentus can decode.
            return OpenManagedWebmOpusOrThrow(filePath);
        }
        catch
        {
            mf?.Dispose();
            throw;
        }
    }

    private static IAudioDecoder OpenWebMWithFallback(Stream stream, string originName, string? contentTypeHint)
    {
        if (!stream.CanSeek)
        {
            throw new NotSupportedException(
                "WebM stream decoding requires a seekable byte stream.");
        }

        IAudioDecoder? mf = null;
        try
        {
            mf = WaveStreamAudioDecoder.OpenMediaFoundation(
                stream, originName, contentTypeHint, ownStream: false);
            var probed = TryProbePcm(mf, out var leftover, out var leftoverLength);
            if (probed > 0)
            {
                IAudioDecoder decorated = leftoverLength > 0
                    ? new PrefixedAudioDecoder(mf, leftover!, leftoverLength)
                    : mf;
                mf = null;
                return new OwningStreamAudioDecoder(decorated, stream);
            }

            if (!IsSubstantialKnownDuration(mf))
            {
                var empty = mf;
                mf = null;
                return new OwningStreamAudioDecoder(empty, stream);
            }

            mf.Dispose();
            mf = null;
            stream.Position = 0;
            return OpenManagedWebmOpusOrThrow(stream, originName);
        }
        catch (Exception ex) when (mf is null && IsMediaFoundationOpenFailure(ex))
        {
            stream.Position = 0;
            return OpenManagedWebmOpusOrThrow(stream, originName);
        }
        catch
        {
            mf?.Dispose();
            throw;
        }
    }

    private static bool IsMediaFoundationOpenFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var name = current.GetType().FullName ?? current.GetType().Name;
            if (name.Contains("MediaFoundation", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var message = current.Message ?? string.Empty;
            if (message.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase)
                || message.Contains("0xC00D", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IAudioDecoder OpenMpeg4WithFallback(string filePath)
    {
        IAudioDecoder? mf = null;
        try
        {
            mf = WaveStreamAudioDecoder.OpenMediaFoundation(filePath);
            var probed = TryProbePcm(mf, out var leftover, out var leftoverLength);
            if (probed > 0)
            {
                return leftoverLength > 0
                    ? new PrefixedAudioDecoder(mf, leftover!, leftoverLength)
                    : mf;
            }

            if (!IsSubstantialKnownDuration(mf))
            {
                // Legitimate empty / unknown-length: keep MF (first Read will EOS).
                return mf;
            }

            mf.Dispose();
            mf = null;
            return OpenManagedMpeg4OrThrow(filePath);
        }
        catch (Exception ex) when (mf is not null && ex is not AudioException)
        {
            mf.Dispose();
            throw;
        }
        catch
        {
            mf?.Dispose();
            throw;
        }
    }

    private static IAudioDecoder OpenMpeg4WithFallback(Stream stream, string originName, string? contentTypeHint)
    {
        if (!stream.CanSeek)
        {
            throw new NotSupportedException(
                "MPEG-4 stream decoding requires a seekable byte stream.");
        }

        IAudioDecoder? mf = null;
        try
        {
            // Probe without taking stream ownership so a managed fallback can reuse it.
            mf = WaveStreamAudioDecoder.OpenMediaFoundation(
                stream, originName, contentTypeHint, ownStream: false);
            var probed = TryProbePcm(mf, out var leftover, out var leftoverLength);
            if (probed > 0)
            {
                IAudioDecoder decorated = leftoverLength > 0
                    ? new PrefixedAudioDecoder(mf, leftover!, leftoverLength)
                    : mf;
                mf = null;
                return new OwningStreamAudioDecoder(decorated, stream);
            }

            if (!IsSubstantialKnownDuration(mf))
            {
                // Legitimate empty: keep MF and take stream ownership.
                var empty = mf;
                mf = null;
                return new OwningStreamAudioDecoder(empty, stream);
            }

            mf.Dispose();
            mf = null;
            stream.Position = 0;
            return OpenManagedMpeg4OrThrow(stream, originName);
        }
        catch
        {
            mf?.Dispose();
            throw;
        }
    }

    private static IAudioDecoder EnsureProducesPcmOrThrow(IAudioDecoder decoder, string originName)
    {
        var duration = decoder.Duration;
        var substantial = IsSubstantialKnownDuration(decoder);
        int probed;
        float[]? leftover;
        int leftoverLength;
        try
        {
            probed = TryProbePcm(decoder, out leftover, out leftoverLength);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }

        if (probed > 0)
        {
            return leftoverLength > 0
                ? new PrefixedAudioDecoder(decoder, leftover!, leftoverLength)
                : decoder;
        }

        if (!substantial)
        {
            return decoder;
        }

        decoder.Dispose();
        throw new DecoderException(
            $"The audio source '{originName}' opened successfully but produced no PCM "
            + $"(declared duration {duration}).");
    }

    private static bool IsSubstantialKnownDuration(IAudioDecoder decoder)
        => decoder.HasKnownDuration && decoder.Duration >= SubstantialDurationThreshold;

    private static int TryProbePcm(IAudioDecoder decoder, out float[]? leftover, out int leftoverLength)
    {
        leftover = new float[OpenProbeSamples];
        leftoverLength = 0;
        try
        {
            var read = decoder.Read(leftover);
            if (read <= 0)
            {
                leftover = null;
                leftoverLength = 0;
                return 0;
            }

            leftoverLength = read;
            return read;
        }
        catch
        {
            leftover = null;
            leftoverLength = 0;
            throw;
        }
    }

    private static AudioContainerKind SniffHeader(ReadOnlySpan<byte> h, Stream? sourceForOgg)
    {
        var read = h.Length;
        if (read < 12)
        {
            return AudioContainerKind.Unknown;
        }

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
            return SniffOgg(sourceForOgg, h);
        }

        if (read >= 8 && HasAscii(h, 4, "ftyp"))
        {
            return ClassifyMpeg4(h, sourceForOgg);
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

    private static AudioContainerKind ClassifyMpeg4(ReadOnlySpan<byte> header, Stream? stream)
    {
        if (HasFragmentedMpeg4Brand(header))
        {
            return AudioContainerKind.FragmentedMpeg4;
        }

        if (stream is { CanSeek: true })
        {
            if (ContainsFragmentedMpeg4Boxes(stream))
            {
                return AudioContainerKind.FragmentedMpeg4;
            }
        }
        else if (stream is PeekBufferStream peek
                 && ContainsAsciiBoxType(peek.PrefetchSpan, "mvex"u8, "moof"u8))
        {
            return AudioContainerKind.FragmentedMpeg4;
        }

        return AudioContainerKind.Mpeg4;
    }

    private static bool HasFragmentedMpeg4Brand(ReadOnlySpan<byte> header)
    {
        // ftyp: size(4) + 'ftyp'(4) + major_brand(4) + minor_version(4) + compatible_brands...
        if (header.Length < 16 || !HasAscii(header, 4, "ftyp"))
        {
            return false;
        }

        var boxSize = (int)ReadUInt32BigEndian(header);
        if (boxSize < 16)
        {
            boxSize = header.Length;
        }

        var end = Math.Min(header.Length, boxSize);
        // Brands start at offset 8 within the box payload → absolute offset 8 from file start of box = 8.
        // major at 8, compatible from 16.
        for (var offset = 8; offset + 4 <= end; offset += 4)
        {
            if (offset == 12)
            {
                // minor_version — skip
                continue;
            }

            if (IsFragmentedBrand(header.Slice(offset, 4)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFragmentedBrand(ReadOnlySpan<byte> brand4)
        => brand4.SequenceEqual("dash"u8)
           || brand4.SequenceEqual("iso5"u8)
           || brand4.SequenceEqual("iso6"u8)
           || brand4.SequenceEqual("iso9"u8)
           || brand4.SequenceEqual("msdh"u8)
           || brand4.SequenceEqual("msix"u8)
           || brand4.SequenceEqual("cmfc"u8)
           || brand4.SequenceEqual("cmaf"u8);

    private static bool ContainsFragmentedMpeg4Boxes(Stream stream)
    {
        var start = stream.Position;
        try
        {
            // Caller already read a small header; scan from the start of the file/window.
            // Sniff restored position in Sniff(); here we are called from SniffHeader with the
            // stream still advanced — walk from current-64 approx. Prefer absolute start scan.
            var scanFrom = Math.Max(0, start - 64);
            stream.Position = scanFrom;
            var remaining = stream.CanSeek ? Math.Max(0, stream.Length - scanFrom) : SniffBufferSize;
            var toRead = (int)Math.Min(Math.Max(remaining, 64), 65_536);
            var buffer = new byte[toRead];
            var n = stream.Read(buffer);
            return ContainsAsciiBoxType(buffer.AsSpan(0, n), "mvex"u8, "moof"u8);
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static bool ContainsAsciiBoxType(ReadOnlySpan<byte> data, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => IndexOf(data, a) >= 0 || IndexOf(data, b) >= 0;

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> box)
    {
        if (box.Length < 4)
        {
            return 0;
        }

        return ((uint)box[0] << 24) | ((uint)box[1] << 16) | ((uint)box[2] << 8) | box[3];
    }

    private static AudioContainerKind SniffOgg(Stream? stream, ReadOnlySpan<byte> firstPage)
    {
        if (stream is PeekBufferStream peek)
        {
            return ClassifyOggPayload(peek.PrefetchSpan);
        }

        if (stream is null || !stream.CanSeek)
        {
            return ClassifyOggPayload(firstPage);
        }

        // Prefer scanning the first ~8 KiB for OpusHead / vorbis identification.
        // Caller already consumed firstPage.Length bytes from the stream.
        var oggStart = stream.Position - firstPage.Length;
        if (oggStart < 0)
        {
            oggStart = 0;
        }

        try
        {
            stream.Position = oggStart;
            var available = (int)Math.Max(0, stream.Length - oggStart);
            var buffer = new byte[Math.Min(SniffBufferSize, Math.Max(available, firstPage.Length))];
            var n = stream.Read(buffer);
            return ClassifyOggPayload(buffer.AsSpan(0, n));
        }
        finally
        {
            stream.Position = oggStart;
        }
    }

    private static AudioContainerKind ClassifyOggPayload(ReadOnlySpan<byte> span)
    {
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

    private static AudioContainerKind? GuessFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var ct = contentType.Trim();
        var semi = ct.IndexOf(';');
        if (semi >= 0)
        {
            ct = ct[..semi].Trim();
        }

        return ct.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/wave" or "audio/x-wav" => AudioContainerKind.Wave,
            "audio/aiff" or "audio/x-aiff" => AudioContainerKind.Aiff,
            "audio/flac" or "audio/x-flac" => AudioContainerKind.Flac,
            "audio/mpeg" or "audio/mp3" => AudioContainerKind.MpegAudio,
            "audio/mp4" or "audio/m4a" or "audio/aac" or "audio/x-m4a" => AudioContainerKind.Mpeg4,
            "audio/webm" => AudioContainerKind.WebM,
            "audio/x-ms-wma" or "video/x-ms-asf" => AudioContainerKind.Asf,
            "audio/opus" => AudioContainerKind.OggOpus,
            "audio/ogg" or "application/ogg" => null, // may be Vorbis or Opus — require sniff
            _ => null,
        };
    }

    private static AudioContainerKind? GuessFromOriginName(string? originName)
    {
        if (string.IsNullOrWhiteSpace(originName))
        {
            return null;
        }

        var path = originName;
        if (Uri.TryCreate(originName, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        var ext = Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".wav" => AudioContainerKind.Wave,
            ".aiff" or ".aif" or ".aifc" => AudioContainerKind.Aiff,
            ".flac" => AudioContainerKind.Flac,
            ".mp3" => AudioContainerKind.MpegAudio,
            ".m4a" or ".mp4" or ".aac" => AudioContainerKind.Mpeg4,
            ".wma" or ".asf" => AudioContainerKind.Asf,
            ".webm" => AudioContainerKind.WebM,
            ".opus" => AudioContainerKind.OggOpus,
            ".ogg" or ".oga" => null,
            _ => null,
        };
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

        return new InvalidAudioException($"The audio source '{filePath}' could not be opened.", ex);
    }
}
