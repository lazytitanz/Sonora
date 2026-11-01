using NAudio.Wave;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Adapts an NAudio <see cref="WaveStream"/> to interleaved float32 <see cref="IAudioDecoder"/>.
/// Media Foundation readers may be recreated after EOF/invalid-state seeks.
/// </summary>
internal sealed class WaveStreamAudioDecoder : IAudioDecoder
{
    private readonly string? _sourcePath;
    private readonly bool _isMediaFoundation;
    private WaveStream _stream;
    private ISampleProvider _samples;
    private readonly float[] _conversionScratch;
    private bool _disposed;
    private bool _reachedEnd;

    private WaveStreamAudioDecoder(WaveStream stream, string? sourcePath, bool isMediaFoundation)
    {
        _stream = stream;
        _sourcePath = sourcePath;
        _isMediaFoundation = isMediaFoundation;
        _samples = stream.ToSampleProvider();
        var wf = _samples.WaveFormat;
        Format = AudioFormat.CreateFloat32(wf.SampleRate, wf.Channels);
        Duration = stream.TotalTime;
        CanSeek = stream.CanSeek;
        _conversionScratch = new float[Math.Max(1024, wf.Channels * 256)];
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; private set; }
    public bool CanSeek { get; }

    public static WaveStreamAudioDecoder OpenWave(string filePath)
        => new(new WaveFileReader(filePath), sourcePath: null, isMediaFoundation: false);

    public static WaveStreamAudioDecoder OpenAiff(string filePath)
        => new(new AiffFileReader(filePath), sourcePath: null, isMediaFoundation: false);

    public static WaveStreamAudioDecoder OpenMediaFoundation(string filePath)
        => new(CreateMediaFoundationReader(filePath), filePath, isMediaFoundation: true);

    public static WaveStreamAudioDecoder FromWaveStream(WaveStream stream, string? sourcePath, bool isMediaFoundation)
        => new(stream, sourcePath, isMediaFoundation);

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
                RecreateMediaFoundationReader();
                if (!TrySeekInPlace(position))
                {
                    throw new DecoderException(
                        $"Failed to seek in '{Describe()}' even after recreating the Media Foundation reader.");
                }
            }

            _reachedEnd = false;
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

    private void RecreateMediaFoundationReader()
    {
        if (!_isMediaFoundation || string.IsNullOrWhiteSpace(_sourcePath))
        {
            throw new DecoderException("Cannot recreate a non-Media-Foundation decoder.");
        }

        var replacement = CreateMediaFoundationReader(_sourcePath);
        var old = _stream;
        _stream = replacement;
        _samples = replacement.ToSampleProvider();
        if (replacement.TotalTime > TimeSpan.Zero)
        {
            Duration = replacement.TotalTime;
        }

        old.Dispose();
        _reachedEnd = false;
    }

    private static MediaFoundationReader CreateMediaFoundationReader(string filePath)
        => new(filePath, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RepositionInRead = false,
            RequestFloatOutput = true,
        });

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

    private string Describe() => _stream.GetType().Name;
}
