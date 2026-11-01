using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ATL;
using Sonora.Audio.Metadata;

namespace Sonora.Audio.Tests.Metadata;

/// <summary>
/// Builds small deterministic tagged fixtures for metadata tests.
/// Fixtures are written under the test output directory and are MIT-licensed synthetic tones.
/// </summary>
internal static class MetadataFixtureHelper
{
    private static readonly object Gate = new();
    private static string? _root;

    public static string Root
    {
        get
        {
            EnsureRoot();
            return _root!;
        }
    }

    public static string WavBasic => Path.Combine(Root, "basic.wav");
    public static string WavNoTags => Path.Combine(Root, "notags.wav");
    public static string WavUnicode => Path.Combine(Root, "unicode.wav");
    public static string WavPunctuationArtist => Path.Combine(Root, "punct-artist.wav");
    public static string WavMultiArtistNative => Path.Combine(Root, "multi-artist.wav");
    public static string WavWithLargeArt => Path.Combine(Root, "large-art.wav");
    public static string WavFrontBackArt => Path.Combine(Root, "front-back-art.wav");
    public static string WavMissingTitle => Path.Combine(Root, "missing-title.wav");

    public static void EnsureAll()
    {
        EnsureRoot();
        lock (Gate)
        {
            AtlSettingsPolicy.EnsureApplied();
            WriteWavIfMissing(WavNoTags, tag: null);
            WriteWavIfMissing(WavBasic, track =>
            {
                track.Title = "Sonora Basic";
                track.Artist = "Test Artist";
                track.AlbumArtist = "Album Artist";
                track.Album = "Test Album";
                track.Genre = "Electronic";
                track.TrackNumber = 2;
                track.TrackTotal = 10;
                track.DiscNumber = 1;
                track.DiscTotal = 2;
                track.Year = 2024;
                track.Composer = "Composer";
                track.Comment = "Comment";
                track.Copyright = "Copyright";
                track.Publisher = "Publisher";
                track.BPM = 120;
            });
            WriteWavIfMissing(WavUnicode, track =>
            {
                track.Title = "タイトル";
                track.Artist = "アーティスト";
                track.Album = "アルバム";
            });
            WriteWavIfMissing(WavPunctuationArtist, track =>
            {
                // Single artist value that contains a semicolon — must not be split.
                track.Title = "Punctuation";
                track.Artist = "AC;DC Tribute";
                track.Genre = "Rock;Roll Legacy";
            });
            WriteWavIfMissing(WavMultiArtistNative, track =>
            {
                track.Title = "Native Multi";
                track.Artist = "Solo";
                track.AdditionalFields["ARTISTS"] =
                    "First" + Settings.DisplayValueSeparator + "Second";
            });
            WriteWavIfMissing(WavMissingTitle, track =>
            {
                track.Artist = "Only Artist";
                track.Title = "";
            });
            WriteWavIfMissing(WavWithLargeArt, track =>
            {
                track.Title = "Large Art";
                track.Artist = "Cover Band";
                var jpeg = CreateSolidJpeg(64, 64, fill: 0x40, repeatPayloadBytes: 256 * 1024);
                track.EmbeddedPictures.Add(
                    PictureInfo.fromBinaryData(jpeg, PictureInfo.PIC_TYPE.Front));
            });
            WriteWavIfMissing(WavFrontBackArt, track =>
            {
                track.Title = "Covers";
                var front = CreateSolidJpeg(16, 16, fill: 0x10);
                var back = CreateSolidJpeg(16, 16, fill: 0x20);
                track.EmbeddedPictures.Add(
                    PictureInfo.fromBinaryData(back, PictureInfo.PIC_TYPE.Back));
                track.EmbeddedPictures.Add(
                    PictureInfo.fromBinaryData(front, PictureInfo.PIC_TYPE.Front));
            });
        }
    }

    /// <summary>
    /// Tags a copy of an existing audio file for format-matrix verification.
    /// Returns null when the source file is missing.
    /// </summary>
    public static string? PrepareTaggedCopy(
        string sourcePath,
        string outputFileName,
        Action<Track> tag)
    {
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        EnsureRoot();
        var dest = Path.Combine(Root, outputFileName);
        lock (Gate)
        {
            if (File.Exists(dest))
            {
                return dest;
            }

            File.Copy(sourcePath, dest, overwrite: false);
            AtlSettingsPolicy.EnsureApplied();
            var track = new Track(dest);
            tag(track);
            if (!track.Save())
            {
                File.Delete(dest);
                throw new InvalidOperationException($"ATL failed to save tags for '{dest}'.");
            }
        }

        return dest;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureRoot()
    {
        if (_root is not null)
        {
            return;
        }

        lock (Gate)
        {
            _root ??= Path.Combine(Path.GetTempPath(), "sonora-metadata-fixtures", "v1");
            Directory.CreateDirectory(_root);
        }
    }

    private static void WriteWavIfMissing(string path, Action<Track>? tag)
    {
        if (File.Exists(path))
        {
            return;
        }

        WriteSilentWav(path, sampleRate: 44100, channels: 1, durationSeconds: 0.25);
        if (tag is null)
        {
            return;
        }

        var track = new Track(path);
        tag(track);
        if (!track.Save())
        {
            File.Delete(path);
            throw new InvalidOperationException($"ATL failed to save tags for '{path}'.");
        }
    }

    private static void WriteSilentWav(string path, int sampleRate, int channels, double durationSeconds)
    {
        var frameCount = (int)Math.Round(sampleRate * durationSeconds);
        var dataLength = frameCount * channels * sizeof(short);
        var bytes = new byte[44 + dataLength];
        var span = bytes.AsSpan();

        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        // Sample payload remains zeros (silence).
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Builds a minimal JPEG and optionally appends junk inside a comment-like padding region
    /// by repeating scan payload to inflate size for allocation tests.
    /// </summary>
    public static byte[] CreateSolidJpeg(int width, int height, byte fill, int repeatPayloadBytes = 0)
    {
        // Extremely small grayscale JPEG-ish payload is hard without an encoder.
        // Use a valid 1x1 JPEG and pad with APP0-incompatible trailing bytes after EOI is NOT valid.
        // Instead create a PNG (lossless, easy) — ATL accepts it as embedded picture binary.
        return CreatePng(width, height, fill, repeatPayloadBytes);
    }

    public static byte[] CreatePng(int width, int height, byte fill, int extraBytes = 0)
    {
        // Minimal valid 8-bit grayscale PNG.
        using var ms = new MemoryStream();
        // PNG signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // grayscale
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(ms, "IHDR", ihdr);

        // Raw image: each row starts with filter byte 0.
        var raw = new byte[(width + 1) * height + Math.Max(0, extraBytes)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width + 1);
            raw[row] = 0;
            raw.AsSpan(row + 1, width).Fill(fill);
        }

        if (extraBytes > 0)
        {
            // Incompressible padding so embedded picture remains large after zlib.
            var pad = raw.AsSpan(raw.Length - extraBytes);
            RandomNumberGenerator.Fill(pad);
        }

        var compressed = CompressZlib(raw);
        WriteChunk(ms, "IDAT", compressed);
        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        stream.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static byte[] CompressZlib(byte[] raw)
    {
        using var output = new MemoryStream();
        // zlib header
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        var adler = Adler32(raw);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, adler);
        output.Write(checksum);
        return output.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var t in data)
        {
            a = (a + t) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
