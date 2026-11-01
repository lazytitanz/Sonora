using NAudio.Wave;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Adapts an NAudio <see cref="WaveStream"/> to interleaved float32 <see cref="IAudioDecoder"/>.
/// Media Foundation readers may be recreated after EOF/invalid-state seeks.
/// MF container timestamps are hints only and never establish Exact accuracy.
/// </summary>
internal sealed class WaveStreamAudioDecoder : IAudioDecoder
{
    /// <summary>Maximum forward-discard window after a coarse seek (source time).</summary>
    private static readonly TimeSpan MaxDiscardWindow = TimeSpan.FromSeconds(2);

    /// <summary>Earlier-anchor skew used once when a coarse seek lands late of the request.</summary>
    private static readonly TimeSpan LateLandAnchorSkew = TimeSpan.FromMilliseconds(100);

    private readonly string? _sourcePath;
    private readonly Func<WaveStream>? _streamFactory;
    private readonly Stream? _ownedByteStream;
    private readonly bool _isMediaFoundation;
    private readonly bool _supportsExactByteSeek;
    private WaveStream _stream;
    private ISampleProvider _samples;
    private readonly float[] _conversionScratch;
    private bool _disposed;
    private bool _reachedEnd;

    private WaveStreamAudioDecoder(
        WaveStream stream,
        string? sourcePath,
        bool isMediaFoundation,
        bool supportsExactByteSeek,
        bool hasKnownDuration,
        Func<WaveStream>? streamFactory = null,
        Stream? ownedByteStream = null)
    {
        _stream = stream;
        _sourcePath = sourcePath;
        _isMediaFoundation = isMediaFoundation;
        _supportsExactByteSeek = supportsExactByteSeek;
        _streamFactory = streamFactory;
        _ownedByteStream = ownedByteStream;
        _samples = stream.ToSampleProvider();
        var wf = _samples.WaveFormat;
        Format = AudioFormat.CreateFloat32(wf.SampleRate, wf.Channels);
        Duration = TryGetTotalTime(stream);
        HasKnownDuration = hasKnownDuration;
        CanSeek = stream.CanSeek;
        _conversionScratch = new float[Math.Max(1024, wf.Channels * 256)];
    }

    private static TimeSpan TryGetTotalTime(WaveStream stream)
    {
        try
        {
            return stream.TotalTime;
        }
        catch (NotSupportedException)
        {
            return TimeSpan.Zero;
        }
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; private set; }
    public bool HasKnownDuration { get; }
    public bool CanSeek { get; }

    public static WaveStreamAudioDecoder OpenWave(string filePath)
        => new(
            new WaveFileReader(filePath),
            sourcePath: null,
            isMediaFoundation: false,
            supportsExactByteSeek: true,
            hasKnownDuration: true);

    public static WaveStreamAudioDecoder OpenWave(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new WaveFileReader(stream);
        return new(
            reader,
            sourcePath: null,
            isMediaFoundation: false,
            supportsExactByteSeek: stream.CanSeek,
            hasKnownDuration: DetermineDurationKnown(reader, isFileOpen: false),
            ownedByteStream: stream);
    }

    public static WaveStreamAudioDecoder OpenAiff(string filePath)
        => new(
            new AiffFileReader(filePath),
            sourcePath: null,
            isMediaFoundation: false,
            supportsExactByteSeek: true,
            hasKnownDuration: true);

    public static WaveStreamAudioDecoder OpenAiff(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new AiffFileReader(stream);
        return new(
            reader,
            sourcePath: null,
            isMediaFoundation: false,
            supportsExactByteSeek: stream.CanSeek,
            hasKnownDuration: DetermineDurationKnown(reader, isFileOpen: false),
            ownedByteStream: stream);
    }

    public static WaveStreamAudioDecoder OpenMediaFoundation(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return new(
            CreateMediaFoundationReader(filePath),
            filePath,
            isMediaFoundation: true,
            supportsExactByteSeek: false,
            hasKnownDuration: true,
            streamFactory: () => CreateMediaFoundationReader(filePath));
    }

    public static WaveStreamAudioDecoder OpenMediaFoundation(
        Stream stream,
        string? originName = null,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new NotSupportedException(
                "Media Foundation stream decoding requires a seekable byte stream (for example HTTP with Range support).");
        }

        Func<WaveStream> factory = () =>
        {
            stream.Position = 0;
            return CreateStreamMediaFoundationReader(stream, originName, contentType);
        };

        var reader = factory();
        return new(
            reader,
            sourcePath: originName,
            isMediaFoundation: true,
            supportsExactByteSeek: false,
            hasKnownDuration: DetermineDurationKnown(reader, isFileOpen: false),
            streamFactory: factory,
            ownedByteStream: stream);
    }

    public static WaveStreamAudioDecoder FromWaveStream(
        WaveStream stream,
        string? sourcePath,
        bool isMediaFoundation,
        bool supportsExactByteSeek = false,
        bool? hasKnownDuration = null,
        Func<WaveStream>? streamFactory = null,
        Stream? ownedByteStream = null)
        => new(
            stream,
            sourcePath,
            isMediaFoundation,
            supportsExactByteSeek,
            hasKnownDuration ?? DetermineDurationKnown(
                stream,
                // File-path opens do not pass an owned byte stream; stream opens do.
                isFileOpen: ownedByteStream is null),
            streamFactory,
            ownedByteStream);

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.IsEmpty)
        {
            return 0;
        }

        try
        {
            var count = Math.Min(destination.Length, _conversionScratch.Length);
            var read = _samples.Read(_conversionScratch.AsSpan(0, count));
            if (read > 0)
            {
                _conversionScratch.AsSpan(0, read).CopyTo(destination);
                return read;
            }

            _reachedEnd = true;
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new DecoderException($"Failed while decoding '{Describe()}'.", ex);
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
            if (_supportsExactByteSeek)
            {
                return SeekExactPcm(requestedFrames, requested);
            }

            return SeekApproximateOrUnknown(requestedFrames, requested);
        }
        catch (Exception ex) when (ex is DecoderException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new DecoderException($"Failed to seek in '{Describe()}'.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _ownedByteStream?.Dispose();
    }

    private AudioSeekResult SeekExactPcm(long requestedFrames, TimeSpan requested)
    {
        CoarseSeekOrRecreate(requested);
        AlignExactBytePosition(requestedFrames);

        var landingFrames = ReadVerifiedFramePosition();
        if (landingFrames is null)
        {
            return AudioSeekResult.Unknown(requested);
        }

        if (landingFrames.Value < requestedFrames)
        {
            DiscardFrames(requestedFrames - landingFrames.Value);
            landingFrames = ReadVerifiedFramePosition() ?? requestedFrames;
        }

        var actual = Format.FramesToTime(landingFrames.Value);
        if (landingFrames.Value == requestedFrames)
        {
            return AudioSeekResult.Exact(requested, actual);
        }

        // Byte math available but landed off the request after bounded discard.
        return AudioSeekResult.Approximate(requested, actual);
    }

    private AudioSeekResult SeekApproximateOrUnknown(long requestedFrames, TimeSpan requested)
    {
        // Media Foundation / compressed paths: CurrentTime is a hint only.
        // Forward discard may improve proximity but never establishes Exact accuracy,
        // and CurrentTime alone is not meaningful evidence for ActualPosition.
        if (!CoarseSeekOrRecreate(requested))
        {
            return AudioSeekResult.Unknown(requested);
        }

        var hintFrames = ReadHintFramePosition();
        if (hintFrames is long early && early < requestedFrames)
        {
            var toDiscard = requestedFrames - early;
            var maxDiscard = Format.TimeToFrames(MaxDiscardWindow);
            DiscardFrames(Math.Min(toDiscard, maxDiscard));
        }
        else if (hintFrames is long late && late > requestedFrames && requestedFrames > 0)
        {
            var anchorFrames = Math.Max(0, requestedFrames - Format.TimeToFrames(LateLandAnchorSkew));
            var anchor = Format.FramesToTime(anchorFrames);
            if (CoarseSeekOrRecreate(anchor))
            {
                var retryHint = ReadHintFramePosition();
                if (retryHint is long h && h < requestedFrames)
                {
                    var toDiscard = requestedFrames - h;
                    var maxDiscard = Format.TimeToFrames(MaxDiscardWindow);
                    DiscardFrames(Math.Min(toDiscard, maxDiscard));
                }
            }
        }

        // Conservative: MF/container timestamps do not prove the first retained frame.
        return AudioSeekResult.Unknown(requested);
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
            var maxFrames = Format.TimeToFrames(Duration);
            frames = AudioFormat.ClampFrameIndex(frames, maxFrames);
        }
        else if (frames < 0)
        {
            frames = 0;
        }

        return frames;
    }

    private bool CoarseSeekOrRecreate(TimeSpan position)
    {
        if (TrySeekInPlace(position))
        {
            _reachedEnd = false;
            return true;
        }

        if (!_isMediaFoundation)
        {
            throw new DecoderException($"Failed to seek in '{Describe()}'.");
        }

        RecreateMediaFoundationReader();
        if (!TrySeekInPlace(position))
        {
            throw new DecoderException(
                $"Failed to seek in '{Describe()}' even after recreating the Media Foundation reader.");
        }

        _reachedEnd = false;
        return true;
    }

    private bool TrySeekInPlace(TimeSpan position)
    {
        try
        {
            _stream.CurrentTime = position;
            _samples = _stream.ToSampleProvider();
            return true;
        }
        catch (Exception ex) when (_isMediaFoundation && (_reachedEnd || IsInvalidState(ex)))
        {
            return false;
        }
    }

    private void AlignExactBytePosition(long requestedFrames)
    {
        var bytesPerFrame = _stream.WaveFormat.BlockAlign;
        if (bytesPerFrame <= 0)
        {
            return;
        }

        var target = checked(requestedFrames * bytesPerFrame);
        if (target > _stream.Length)
        {
            target = _stream.Length - (_stream.Length % bytesPerFrame);
        }

        if (target < 0)
        {
            target = 0;
        }

        _stream.Position = target;
        _samples = _stream.ToSampleProvider();
    }

    private long? ReadVerifiedFramePosition()
    {
        if (!_supportsExactByteSeek)
        {
            return null;
        }

        var bytesPerFrame = _stream.WaveFormat.BlockAlign;
        if (bytesPerFrame <= 0)
        {
            return null;
        }

        return _stream.Position / bytesPerFrame;
    }

    /// <summary>Hint only — never treat as verified source-frame proof.</summary>
    private long? ReadHintFramePosition()
    {
        try
        {
            if (_stream.CurrentTime < TimeSpan.Zero)
            {
                return null;
            }

            return Format.TimeToFrames(_stream.CurrentTime);
        }
        catch
        {
            return null;
        }
    }

    private void DiscardFrames(long framesToDiscard)
    {
        if (framesToDiscard <= 0)
        {
            return;
        }

        var channels = Format.ChannelCount;
        var remaining = checked(framesToDiscard * channels);
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, _conversionScratch.Length);
            // Trim to whole frames.
            chunk -= chunk % channels;
            if (chunk <= 0)
            {
                break;
            }

            var read = _samples.Read(_conversionScratch.AsSpan(0, chunk));
            if (read <= 0)
            {
                _reachedEnd = true;
                break;
            }

            // Keep frame alignment if the provider returns a partial block.
            var frameAligned = read - (read % channels);
            remaining -= frameAligned;
            if (frameAligned <= 0)
            {
                break;
            }
        }
    }

    private void RecreateMediaFoundationReader()
    {
        if (!_isMediaFoundation || _streamFactory is null)
        {
            throw new DecoderException("Cannot recreate Media Foundation reader without a stream factory.");
        }

        var replacement = _streamFactory();
        var old = _stream;
        _stream = replacement;
        _samples = replacement.ToSampleProvider();
        if (replacement.TotalTime > TimeSpan.Zero)
        {
            Duration = replacement.TotalTime;
        }

        // Dispose the WaveStream only — owned byte streams are retained for recreate.
        old.Dispose();
        _reachedEnd = false;
    }

    private static bool DetermineDurationKnown(WaveStream stream, bool isFileOpen)
    {
        if (isFileOpen)
        {
            return true;
        }

        try
        {
            if (stream.TotalTime > TimeSpan.Zero)
            {
                return true;
            }
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (!stream.CanSeek)
        {
            return false;
        }

        try
        {
            _ = stream.Length;
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static MediaFoundationReader CreateMediaFoundationReader(string filePath)
        => new(filePath, CreateMediaFoundationSettings());

    private static StreamMediaFoundationReader CreateStreamMediaFoundationReader(
        Stream stream,
        string? originName,
        string? contentType)
        => new(stream, CreateMediaFoundationSettings(), contentType, originName);

    private static MediaFoundationReader.MediaFoundationReaderSettings CreateMediaFoundationSettings()
        => new()
        {
            RepositionInRead = false,
            RequestFloatOutput = true,
        };

    private static bool IsInvalidState(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("invalid in the current state", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string Describe() => _sourcePath ?? _stream.GetType().Name;
}
