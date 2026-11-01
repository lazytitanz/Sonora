using Concentus;
using Concentus.Oggfile;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Ogg Opus decoder backed by Concentus + Concentus.Oggfile (managed, MIT).
/// </summary>
internal sealed class OpusOggAudioDecoder : IAudioDecoder
{
    private const int OpusSampleRate = 48_000;

    private readonly string _sourcePath;
    private readonly int _channels;
    private FileStream _file;
    private IOpusDecoder _opusDecoder;
    private OpusOggReadStream _ogg;
    private readonly float[] _packetScratch;
    private int _packetOffset;
    private int _packetCount;
    private bool _disposed;
    private bool _reachedEnd;

    private OpusOggAudioDecoder(string sourcePath, int channels, FileStream file, IOpusDecoder opusDecoder, OpusOggReadStream ogg)
    {
        _sourcePath = sourcePath;
        _channels = channels;
        _file = file;
        _opusDecoder = opusDecoder;
        _ogg = ogg;
        Format = AudioFormat.CreateFloat32(OpusSampleRate, channels);
        Duration = ogg.TotalTime > TimeSpan.Zero ? ogg.TotalTime : TimeSpan.Zero;
        CanSeek = ogg.CanSeek;
        // Max Opus packet is 120 ms; at 48 kHz stereo that is 11520 samples.
        _packetScratch = new float[OpusSampleRate / 1000 * 120 * channels];
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; private set; }
    public bool CanSeek { get; }

    public static OpusOggAudioDecoder Open(string filePath)
    {
        var channels = ReadOpusChannelCount(filePath);
        var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var opusDecoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, channels);
            var ogg = new OpusOggReadStream(opusDecoder, file);
            var decoder = new OpusOggAudioDecoder(filePath, channels, file, opusDecoder, ogg);
            if (ogg.TotalTime <= TimeSpan.Zero)
            {
                // Duration may become available after the first pages; keep zero if unknown.
            }

            return decoder;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.IsEmpty)
        {
            return 0;
        }

        try
        {
            var written = 0;
            while (written < destination.Length)
            {
                if (_packetOffset >= _packetCount)
                {
                    if (!TryFillPacket())
                    {
                        _reachedEnd = true;
                        break;
                    }
                }

                var available = _packetCount - _packetOffset;
                var toCopy = Math.Min(available, destination.Length - written);
                _packetScratch.AsSpan(_packetOffset, toCopy).CopyTo(destination.Slice(written, toCopy));
                _packetOffset += toCopy;
                written += toCopy;
            }

            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new DecoderException($"Failed while decoding Opus file '{_sourcePath}'.", ex);
        }
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek)
        {
            throw new NotSupportedException("The current decoder does not support seeking.");
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (Duration > TimeSpan.Zero && position > Duration)
        {
            position = Duration;
        }

        try
        {
            if (!TrySeekInPlace(position))
            {
                Recreate();
                if (!TrySeekInPlace(position))
                {
                    throw new DecoderException($"Failed to seek in Opus file '{_sourcePath}' after recreate.");
                }
            }

            _packetOffset = 0;
            _packetCount = 0;
            _reachedEnd = false;
        }
        catch (Exception ex) when (ex is DecoderException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new DecoderException($"Failed to seek in Opus file '{_sourcePath}'.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ogg.Close();
        _file.Dispose();
    }

    private bool TryFillPacket()
    {
        while (_ogg.HasNextPacket)
        {
            var pcm = _ogg.DecodeNextPacket();
            if (pcm is null || pcm.Length == 0)
            {
                continue;
            }

            var count = Math.Min(pcm.Length, _packetScratch.Length);
            for (var i = 0; i < count; i++)
            {
                _packetScratch[i] = pcm[i] / 32768f;
            }

            _packetCount = count;
            _packetOffset = 0;
            return true;
        }

        return false;
    }

    private bool TrySeekInPlace(TimeSpan position)
    {
        try
        {
            _ogg.SeekTo(position);
            // Concentus may leave the stream unreadable after EOF; treat as failure.
            if (_reachedEnd && !_ogg.HasNextPacket && position < Duration)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Recreate()
    {
        _ogg.Close();
        _file.Dispose();

        _file = new FileStream(_sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _opusDecoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, _channels);
        _ogg = new OpusOggReadStream(_opusDecoder, _file);
        if (_ogg.TotalTime > TimeSpan.Zero)
        {
            Duration = _ogg.TotalTime;
        }

        _packetOffset = 0;
        _packetCount = 0;
        _reachedEnd = false;
    }

    /// <summary>
    /// Reads channel count from the OpusHead identification header inside the Ogg container.
    /// </summary>
    internal static int ReadOpusChannelCount(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var buffer = new byte[Math.Min(8192, (int)stream.Length)];
        var n = stream.Read(buffer);
        var span = buffer.AsSpan(0, n);
        var needle = "OpusHead"u8;
        for (var i = 0; i <= span.Length - needle.Length - 2; i++)
        {
            if (!span.Slice(i, needle.Length).SequenceEqual(needle))
            {
                continue;
            }

            // OpusHead: magic(8) + version(1) + channel_count(1)
            var channels = span[i + 9];
            if (channels is >= 1 and <= 8)
            {
                return channels;
            }
        }

        // Default stereo when header is missing/odd; decoder open will fail if wrong.
        return 2;
    }
}
