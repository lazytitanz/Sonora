using Sonora.Audio.Abstractions;
using Sonora.Audio.Buffers;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// One decoder + pump + ring for either the active or next track.
/// </summary>
internal sealed class TrackDecodePipeline : IDisposable
{
    private DecodePump? _pump;
    private bool _disposed;

    public TrackDecodePipeline(
        string path,
        IAudioDecoder decoder,
        PcmRingBuffer ring,
        TimeSpan duration)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        Ring = ring ?? throw new ArgumentNullException(nameof(ring));
        Duration = duration;
        Format = decoder.Format;
    }

    public string Path { get; }
    public IAudioDecoder Decoder { get; }
    public PcmRingBuffer Ring { get; }
    public TimeSpan Duration { get; }
    public AudioFormat Format { get; }
    public bool IsPumpRunning => _pump?.IsRunning == true;

    public static TrackDecodePipeline Create(
        string path,
        IAudioDecoder decoder,
        AudioEngineOptions options)
    {
        var capacity = Math.Max(
            decoder.Format.ChannelCount * 256,
            (int)Math.Ceiling(options.BufferDuration.TotalSeconds * decoder.Format.SamplesPerSecond));
        var ring = new PcmRingBuffer(capacity);
        return new TrackDecodePipeline(path, decoder, ring, decoder.Duration);
    }

    public void StartPump(
        IReadOnlyList<IAudioProcessor> processors,
        Func<float> getVolume,
        Action<int> onSamplesWritten,
        Action<Exception>? onError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pump?.IsRunning == true)
        {
            return;
        }

        _pump?.Dispose();
        _pump = new DecodePump(
            Decoder,
            Ring,
            processors,
            getVolume,
            onSamplesWritten,
            onError);
        _pump.Start();
    }

    public void StopPump()
    {
        var pump = _pump;
        _pump = null;
        if (pump is null)
        {
            return;
        }

        pump.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        pump.Dispose();
    }

    public void Prime(TimeSpan primeDuration)
    {
        var target = (int)Math.Ceiling(primeDuration.TotalSeconds * Format.SamplesPerSecond);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Ring.Count < target && !Ring.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPump();
        Ring.Dispose();
        Decoder.Dispose();
    }
}
