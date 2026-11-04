using Concentus;
using ManagedCodecs;
using ManagedCodecs.Containers;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;
using IAudioDecoder = Sonora.Audio.Abstractions.IAudioDecoder;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// WebM / Matroska Opus decoder via ManagedCodecs demux + Concentus.
/// Used when Media Foundation rejects a WebM Opus source (<c>MF_E_INVALID_FORMAT</c> and similar).
/// </summary>
internal sealed class ManagedWebmOpusAudioDecoder : IAudioDecoder
{
    private const int OpusSampleRate = 48_000;

    private readonly string _originName;
    private readonly IDemuxer _demuxer;
    private readonly AudioStreamInfo _audio;
    private IOpusDecoder _opus;
    private IEnumerator<Packet>? _packets;
    private readonly float[] _frameScratch;
    private readonly float[] _decodeScratch;
    private int _scratchOffset;
    private int _scratchCount;
    private bool _disposed;

    private ManagedWebmOpusAudioDecoder(
        string originName,
        IDemuxer demuxer,
        AudioStreamInfo audio,
        IOpusDecoder opus,
        int channels)
    {
        _originName = originName;
        _demuxer = demuxer;
        _audio = audio;
        _opus = opus;
        Format = AudioFormat.CreateFloat32(OpusSampleRate, channels);
        Duration = ComputeDuration(audio);
        HasKnownDuration = Duration > TimeSpan.Zero;
        CanSeek = true;
        // Max Opus packet is 120 ms.
        _frameScratch = new float[OpusSampleRate / 1000 * 120 * channels];
        _decodeScratch = new float[_frameScratch.Length];
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; }
    public bool HasKnownDuration { get; }
    public bool CanSeek { get; }

    public static ManagedWebmOpusAudioDecoder Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        IDemuxer? demuxer = null;
        try
        {
            demuxer = Demuxer.Open(filePath);
            return Create(filePath, demuxer);
        }
        catch
        {
            demuxer?.Dispose();
            throw;
        }
    }

    public static ManagedWebmOpusAudioDecoder Open(Stream stream, string? originName = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new NotSupportedException("Managed WebM Opus decoding requires a seekable stream.");
        }

        var name = string.IsNullOrWhiteSpace(originName) ? stream.GetType().Name : originName;
        IDemuxer? demuxer = null;
        try
        {
            demuxer = Demuxer.Open(stream, leaveOpen);
            return Create(name, demuxer);
        }
        catch
        {
            demuxer?.Dispose();
            if (!leaveOpen)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    private static ManagedWebmOpusAudioDecoder Create(string originName, IDemuxer demuxer)
    {
        var audio = demuxer.Audio
            ?? throw new UnsupportedFormatException($"No audio stream found in '{originName}'.");

        if (!IsOpusCodec(audio.Codec))
        {
            throw new UnsupportedFormatException(
                $"Managed WebM decoding supports Opus only; '{originName}' reports codec '{audio.Codec}'.");
        }

        var channels = audio.Channels > 0 ? audio.Channels : 2;
        IOpusDecoder opus;
        try
        {
            opus = OpusCodecFactory.CreateDecoder(OpusSampleRate, channels);
        }
        catch (Exception ex)
        {
            throw new UnsupportedFormatException(
                $"Could not create an Opus decoder for '{originName}'.", ex);
        }

        return new ManagedWebmOpusAudioDecoder(originName, demuxer, audio, opus, channels);
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
            var channels = Format.ChannelCount;
            while (written < destination.Length)
            {
                if (_scratchOffset >= _scratchCount)
                {
                    if (!TryFillScratch())
                    {
                        break;
                    }
                }

                var available = _scratchCount - _scratchOffset;
                var toCopy = Math.Min(available, destination.Length - written);
                if (channels > 1)
                {
                    toCopy -= toCopy % channels;
                    if (toCopy <= 0)
                    {
                        break;
                    }
                }

                _frameScratch.AsSpan(_scratchOffset, toCopy).CopyTo(destination.Slice(written, toCopy));
                _scratchOffset += toCopy;
                written += toCopy;
            }

            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                       and not ObjectDisposedException
                                       and not DecoderException
                                       and not UnsupportedFormatException
                                       and not InvalidAudioException)
        {
            throw new DecoderException($"Failed while decoding WebM Opus source '{_originName}'.", ex);
        }
    }

    public AudioSeekResult Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek)
        {
            throw new NotSupportedException("The current decoder does not support seeking.");
        }

        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (HasKnownDuration && clamped > Duration)
        {
            clamped = Duration;
        }

        try
        {
            ResetPacketEnumeration();
            _scratchOffset = 0;
            _scratchCount = 0;

            var pts = SecondsToPts(clamped.TotalSeconds);
            _demuxer.Seek(pts, _audio.StreamIndex);
            _opus = OpusCodecFactory.CreateDecoder(OpusSampleRate, Format.ChannelCount);

            var actual = Format.FramesToTime(Format.TimeToFrames(clamped));
            return new AudioSeekResult(clamped, actual, AudioSeekAccuracy.Approximate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                       and not ObjectDisposedException
                                       and not NotSupportedException
                                       and not DecoderException)
        {
            throw new DecoderException($"Failed to seek in '{_originName}'.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetPacketEnumeration();
        _demuxer.Dispose();
    }

    private bool TryFillScratch()
    {
        _packets ??= _demuxer.ReadPackets().GetEnumerator();
        while (_packets.MoveNext())
        {
            var packet = _packets.Current;
            if (packet.StreamIndex != _audio.StreamIndex)
            {
                continue;
            }

            var data = packet.Data.Span;
            if (data.IsEmpty)
            {
                continue;
            }

            var framesPerChannel = _opus.Decode(data, _decodeScratch, _decodeScratch.Length / Format.ChannelCount);
            if (framesPerChannel <= 0)
            {
                continue;
            }

            var interleaved = framesPerChannel * Format.ChannelCount;
            if (interleaved > _frameScratch.Length)
            {
                throw new DecoderException(
                    $"Opus frame in '{_originName}' exceeds scratch capacity ({interleaved} samples).");
            }

            _decodeScratch.AsSpan(0, interleaved).CopyTo(_frameScratch);
            _scratchOffset = 0;
            _scratchCount = interleaved;
            return true;
        }

        return false;
    }

    private void ResetPacketEnumeration()
    {
        _packets?.Dispose();
        _packets = null;
    }

    private long SecondsToPts(double seconds)
    {
        var tb = _audio.TimeBase;
        if (tb.Numerator == 0 || tb.Denominator == 0)
        {
            return (long)Math.Round(seconds * 1000.0);
        }

        return (long)Math.Round(seconds * tb.Denominator / tb.Numerator);
    }

    private static TimeSpan ComputeDuration(AudioStreamInfo audio)
    {
        if (audio.Duration > 0 && audio.TimeBase.Numerator != 0 && audio.TimeBase.Denominator != 0)
        {
            var seconds = audio.Duration * (double)audio.TimeBase.Numerator / audio.TimeBase.Denominator;
            if (seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return TimeSpan.Zero;
    }

    private static bool IsOpusCodec(string codec)
        => string.Equals(codec, "A_OPUS", StringComparison.OrdinalIgnoreCase)
           || codec.Contains("opus", StringComparison.OrdinalIgnoreCase);
}
