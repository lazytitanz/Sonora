using Concentus;
using Concentus.Oggfile;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Ogg Opus decoder backed by Concentus + Concentus.Oggfile (managed, MIT).
/// Seeking is page-granular; container timestamps do not establish Exact accuracy.
/// </summary>
internal sealed class OpusOggAudioDecoder : IAudioDecoder
{
    private const int OpusSampleRate = 48_000;

    /// <summary>Typical Opus preroll used when discarding toward a requested frame after a coarse page seek.</summary>
    private static readonly TimeSpan PrerollHint = TimeSpan.FromMilliseconds(80);

    private static readonly TimeSpan MaxDiscardWindow = TimeSpan.FromSeconds(2);

    private readonly string _originName;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _channels;
    private IOpusDecoder _opusDecoder;
    private OpusOggReadStream _ogg;
    private readonly float[] _packetScratch;
    private int _packetOffset;
    private int _packetCount;
    private bool _disposed;
    private bool _reachedEnd;

    private OpusOggAudioDecoder(
        string originName,
        Stream stream,
        bool leaveOpen,
        int channels,
        IOpusDecoder opusDecoder,
        OpusOggReadStream ogg)
    {
        _originName = originName;
        _stream = stream;
        _leaveOpen = leaveOpen;
        _channels = channels;
        _opusDecoder = opusDecoder;
        _ogg = ogg;
        Format = AudioFormat.CreateFloat32(OpusSampleRate, channels);
        Duration = ogg.TotalTime > TimeSpan.Zero ? ogg.TotalTime : TimeSpan.Zero;
        CanSeek = ogg.CanSeek;
        HasKnownDuration = DetermineDurationKnown(ogg, stream);
        // Max Opus packet is 120 ms; at 48 kHz stereo that is 11520 samples.
        _packetScratch = new float[OpusSampleRate / 1000 * 120 * channels];
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; private set; }
    public bool HasKnownDuration { get; }
    public bool CanSeek { get; }

    public static OpusOggAudioDecoder Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var channels = ReadOpusChannelCount(filePath);
        var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return OpenCore(file, filePath, leaveOpen: false, channels);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public static OpusOggAudioDecoder Open(Stream stream, string? originName = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var name = string.IsNullOrWhiteSpace(originName) ? stream.GetType().Name : originName;
        var channels = ReadOpusChannelCount(stream);
        try
        {
            return OpenCore(stream, name, leaveOpen, channels);
        }
        catch
        {
            if (!leaveOpen)
            {
                stream.Dispose();
            }

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
            throw new DecoderException($"Failed while decoding Opus source '{_originName}'.", ex);
        }
    }

    public AudioSeekResult Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek)
        {
            throw new NotSupportedException("The current decoder does not support seeking.");
        }

        var requestedFrames = ClampRequestedFrames(position);
        var requested = Format.FramesToTime(requestedFrames);

        try
        {
            // Seek slightly early when possible so preroll/discard can approach the request.
            var coarseFrames = Math.Max(0, requestedFrames - Format.TimeToFrames(PrerollHint));
            var coarse = Format.FramesToTime(coarseFrames);

            if (!TrySeekInPlace(coarse))
            {
                Recreate();
                if (!TrySeekInPlace(coarse))
                {
                    throw new DecoderException($"Failed to seek in Opus source '{_originName}' after recreate.");
                }
            }

            _packetOffset = 0;
            _packetCount = 0;
            _reachedEnd = false;

            // Best-effort discard toward the request. Page seeks do not prove frame identity,
            // so ActualPosition stays unset (Unknown).
            if (requestedFrames > coarseFrames)
            {
                var toDiscard = Math.Min(
                    requestedFrames - coarseFrames,
                    Format.TimeToFrames(MaxDiscardWindow));
                DiscardFrames(toDiscard);
            }

            return AudioSeekResult.Unknown(requested);
        }
        catch (Exception ex) when (ex is DecoderException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new DecoderException($"Failed to seek in Opus source '{_originName}'.", ex);
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
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static OpusOggAudioDecoder OpenCore(Stream stream, string originName, bool leaveOpen, int channels)
    {
        var opusDecoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, channels);
        var ogg = new OpusOggReadStream(opusDecoder, stream);
        return new OpusOggAudioDecoder(originName, stream, leaveOpen, channels, opusDecoder, ogg);
    }

    private static bool DetermineDurationKnown(OpusOggReadStream ogg, Stream stream)
    {
        if (ogg.TotalTime > TimeSpan.Zero)
        {
            return true;
        }

        if (!ogg.CanSeek && !stream.CanSeek)
        {
            return false;
        }

        try
        {
            if (stream.CanSeek)
            {
                _ = stream.Length;
                return true;
            }
        }
        catch (NotSupportedException)
        {
            return false;
        }

        return ogg.CanSeek;
    }

    private long ClampRequestedFrames(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        var frames = Format.TimeToFrames(position);
        if (HasKnownDuration && Duration > TimeSpan.Zero)
        {
            frames = AudioFormat.ClampFrameIndex(frames, Format.TimeToFrames(Duration));
        }

        return frames;
    }

    private void DiscardFrames(long framesToDiscard)
    {
        if (framesToDiscard <= 0)
        {
            return;
        }

        var remaining = checked(framesToDiscard * _channels);
        var scratch = new float[Math.Min(_packetScratch.Length, 4096)];
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, scratch.Length);
            chunk -= chunk % _channels;
            if (chunk <= 0)
            {
                break;
            }

            var read = Read(scratch.AsSpan(0, chunk));
            if (read <= 0)
            {
                break;
            }

            var aligned = read - (read % _channels);
            remaining -= aligned;
            if (aligned <= 0)
            {
                break;
            }
        }
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
            if (_reachedEnd && !_ogg.HasNextPacket && (!HasKnownDuration || position < Duration))
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
        if (!_stream.CanSeek)
        {
            throw new DecoderException(
                $"Cannot recreate Opus decoder for non-seekable source '{_originName}'.");
        }

        _ogg.Close();
        _stream.Position = 0;
        _opusDecoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, _channels);
        _ogg = new OpusOggReadStream(_opusDecoder, _stream);
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
        return ReadOpusChannelCount(stream);
    }

    /// <summary>
    /// Reads channel count from the OpusHead identification header.
    /// When <paramref name="stream"/> is seekable, position is restored after the peek.
    /// <see cref="PeekBufferStream"/> uses its prefetched prefix without consuming the stream.
    /// </summary>
    internal static int ReadOpusChannelCount(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream is PeekBufferStream peek)
        {
            return ParseOpusChannelCount(peek.PrefetchSpan);
        }

        long? restore = null;
        if (stream.CanSeek)
        {
            restore = stream.Position;
        }

        try
        {
            var max = 8192;
            if (stream.CanSeek)
            {
                try
                {
                    max = (int)Math.Min(8192, Math.Max(0, stream.Length - stream.Position));
                }
                catch (NotSupportedException)
                {
                    max = 8192;
                }
            }

            var buffer = new byte[Math.Max(64, max)];
            var n = stream.Read(buffer);
            return ParseOpusChannelCount(buffer.AsSpan(0, n));
        }
        finally
        {
            if (restore is long pos)
            {
                stream.Position = pos;
            }
        }
    }

    private static int ParseOpusChannelCount(ReadOnlySpan<byte> span)
    {
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

        return 2;
    }
}
