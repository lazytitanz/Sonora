using ManagedCodecs;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;
using IAudioDecoder = Sonora.Audio.Abstractions.IAudioDecoder;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Fragmented (and progressive) MPEG-4 AAC decoder via ManagedCodecs demux + AAC.
/// Emits interleaved float32 PCM for Sonora's <see cref="IAudioDecoder"/>. Seeking is Approximate.
/// </summary>
internal sealed class ManagedAacMp4AudioDecoder : IAudioDecoder
{
    private readonly string _originName;
    private readonly IDemuxer _demuxer;
    private readonly AudioStreamInfo _audio;
    private AacDecoder _decoder;
    private IEnumerator<Packet>? _packets;
    private readonly float[] _frameScratch;
    private int _scratchOffset;
    private int _scratchCount;
    private long _samplesProduced;
    private bool _disposed;

    private ManagedAacMp4AudioDecoder(string originName, IDemuxer demuxer, AudioStreamInfo audio, AacDecoder decoder)
    {
        _originName = originName;
        _demuxer = demuxer;
        _audio = audio;
        _decoder = decoder;

        var sampleRate = decoder.SampleRate > 0 ? decoder.SampleRate : audio.SampleRate;
        var channels = decoder.Channels > 0 ? decoder.Channels : audio.Channels;
        if (sampleRate <= 0 || channels <= 0)
        {
            demuxer.Dispose();
            throw new UnsupportedFormatException(
                $"AAC stream in '{originName}' did not report a valid sample rate/channel count.");
        }

        Format = AudioFormat.CreateFloat32(sampleRate, channels);
        Duration = ComputeDuration(audio, sampleRate);
        HasKnownDuration = Duration > TimeSpan.Zero;
        CanSeek = true;

        // AAC LC frame is typically 1024 samples/ch; HE-AAC up to 2048. Allocate headroom.
        _frameScratch = new float[Math.Max(4096, sampleRate / 10 * channels)];
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; }
    public bool HasKnownDuration { get; }
    public bool CanSeek { get; }

    /// <summary>Interleaved float samples successfully returned from <see cref="Read"/> so far.</summary>
    internal long SamplesProduced => _samplesProduced;

    public static ManagedAacMp4AudioDecoder Open(string filePath)
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

    public static ManagedAacMp4AudioDecoder Open(Stream stream, string? originName = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new NotSupportedException("Managed MPEG-4 AAC decoding requires a seekable stream.");
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

    private static ManagedAacMp4AudioDecoder Create(string originName, IDemuxer demuxer)
    {
        var audio = demuxer.Audio
            ?? throw new UnsupportedFormatException($"No audio stream found in '{originName}'.");

        if (!IsAacCodec(audio.Codec))
        {
            throw new UnsupportedFormatException(
                $"Managed MPEG-4 decoding supports AAC only; '{originName}' reports codec '{audio.Codec}'.");
        }

        AacDecoder decoder;
        try
        {
            decoder = new AacDecoder(audio);
        }
        catch (NotSupportedException ex)
        {
            throw new UnsupportedFormatException(
                $"The AAC profile in '{originName}' is not supported.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidAudioException(
                $"The AAC configuration in '{originName}' is invalid.", ex);
        }

        return new ManagedAacMp4AudioDecoder(originName, demuxer, audio, decoder);
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
                if (_scratchOffset >= _scratchCount)
                {
                    if (!TryFillScratch())
                    {
                        break;
                    }
                }

                var available = _scratchCount - _scratchOffset;
                var toCopy = Math.Min(available, destination.Length - written);
                // Keep channel alignment when destination is larger than remaining scratch.
                var channels = Format.ChannelCount;
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

            _samplesProduced += written;
            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                       and not ObjectDisposedException
                                       and not DecoderException
                                       and not UnsupportedFormatException
                                       and not InvalidAudioException)
        {
            throw new DecoderException($"Failed while decoding AAC MPEG-4 source '{_originName}'.", ex);
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
            _decoder = new AacDecoder(_audio);

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

            var frame = _decoder.Decode(packet);
            if (frame.SampleCount <= 0 || frame.Channels <= 0)
            {
                continue;
            }

            // HE-AAC may reveal SBR rate/channels after the first access unit.
            if (frame.SampleRate != Format.SampleRate || frame.Channels != Format.ChannelCount)
            {
                throw new DecoderException(
                    $"AAC format changed mid-stream in '{_originName}' "
                    + $"({frame.SampleRate} Hz / {frame.Channels} ch).");
            }

            var interleaved = frame.SampleCount * frame.Channels;
            if (interleaved > _frameScratch.Length)
            {
                throw new DecoderException(
                    $"AAC frame in '{_originName}' exceeds scratch capacity ({interleaved} samples).");
            }

            Interleave(frame, _frameScratch.AsSpan(0, interleaved));
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
            return (long)Math.Round(seconds * Format.SampleRate);
        }

        return (long)Math.Round(seconds * tb.Denominator / tb.Numerator);
    }

    private static TimeSpan ComputeDuration(AudioStreamInfo audio, int sampleRate)
    {
        if (audio.Duration > 0 && audio.TimeBase.Numerator != 0 && audio.TimeBase.Denominator != 0)
        {
            var seconds = audio.Duration * (double)audio.TimeBase.Numerator / audio.TimeBase.Denominator;
            if (seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        if (audio.FrameCount > 0 && sampleRate > 0)
        {
            // AAC LC default frame size; HE-AAC frames are longer — Duration still prefers container ticks.
            var seconds = audio.FrameCount * 1024.0 / sampleRate;
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.Zero;
    }

    private static bool IsAacCodec(string codec)
        => string.Equals(codec, CodecIds.Aac, StringComparison.OrdinalIgnoreCase)
           || codec.Contains("aac", StringComparison.OrdinalIgnoreCase)
           || string.Equals(codec, "mp4a", StringComparison.OrdinalIgnoreCase);

    private static void Interleave(AudioFrame frame, Span<float> destination)
    {
        var n = frame.SampleCount;
        var ch = frame.Channels;
        var i = 0;
        for (var s = 0; s < n; s++)
        {
            for (var c = 0; c < ch; c++)
            {
                destination[i++] = frame.Samples[c][s];
            }
        }
    }
}
