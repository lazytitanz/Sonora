using Sonora.Audio.Abstractions;
using Sonora.Audio.Buffers;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Network;
using Sonora.Audio.Sources;

namespace Sonora.Audio.Playback;

/// <summary>
/// One decoder + pump + ring for either the active or next track.
/// </summary>
internal sealed class TrackDecodePipeline : IDisposable
{
    private DecodePump? _pump;
    /// <summary>
    /// Last pump instance for diagnostics after <see cref="StopPumpAsync"/> nulls <see cref="_pump"/>.
    /// A timed-out stop does not prove the worker exited; this keeps <see cref="IsPumpRunning"/> accurate.
    /// </summary>
    private DecodePump? _observedPump;
    private bool _disposed;
    private int _lastStopTimedOut;
    private long _stopPumpTimeoutCount;
    private Action? _onDecodeWorkerAliveAfterStop;

    public TrackDecodePipeline(
        string path,
        IAudioDecoder decoder,
        PcmRingBuffer ring,
        TimeSpan duration,
        AudioSource? source = null,
        INetworkTransportInfo? networkInfo = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        Ring = ring ?? throw new ArgumentNullException(nameof(ring));
        Duration = duration;
        Format = decoder.Format;
        Source = source;
        NetworkInfo = networkInfo;
    }

    /// <summary>Identifier string (filesystem path or absolute URI).</summary>
    public string Path { get; }

    /// <summary>Typed source when known; <see langword="null"/> for legacy path-only opens.</summary>
    public AudioSource? Source { get; }

    /// <summary>
    /// Network transport probe captured before format conversion wraps the decoder.
    /// Remains valid while this pipeline owns the (possibly wrapped) decoder.
    /// </summary>
    public INetworkTransportInfo? NetworkInfo { get; }

    public IAudioDecoder Decoder { get; }
    public PcmRingBuffer Ring { get; }
    public TimeSpan Duration { get; }
    public AudioFormat Format { get; }
    /// <summary>
    /// True when a decode worker task is still incomplete, including after a StopPump timeout
    /// where the pipeline cleared its active pump reference.
    /// </summary>
    public bool IsPumpRunning => Volatile.Read(ref _observedPump)?.IsRunning == true
        || Volatile.Read(ref _pump)?.IsRunning == true;

    /// <summary>True when the most recent StopPump timed out before the worker joined.</summary>
    public bool LastStopTimedOut => Volatile.Read(ref _lastStopTimedOut) != 0;

    /// <summary>
    /// True when StopPump timed out and the worker may still be inside Decoder.Read/Seek.
    /// Timeout alone is never treated as proof of exit.
    /// </summary>
    public bool HasOrphanDecodeWorker => LastStopTimedOut && IsPumpRunning;

    /// <summary>Cumulative StopPump join timeouts for this pipeline.</summary>
    public long StopPumpTimeoutCount => Volatile.Read(ref _stopPumpTimeoutCount);

    /// <summary>Optional diagnostics callback when a decode worker is still alive after StopPump timeout.</summary>
    public Action? OnDecodeWorkerAliveAfterStop
    {
        get => _onDecodeWorkerAliveAfterStop;
        set => _onDecodeWorkerAliveAfterStop = value;
    }

    public static TrackDecodePipeline Create(
        AudioSource source,
        IAudioDecoder decoder,
        AudioEngineOptions options,
        INetworkTransportInfo? networkInfo = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = Math.Max(
            decoder.Format.ChannelCount * 256,
            (int)Math.Ceiling(options.BufferDuration.TotalSeconds * decoder.Format.SamplesPerSecond));
        var ring = new PcmRingBuffer(capacity);
        return new TrackDecodePipeline(
            source.Identifier,
            decoder,
            ring,
            decoder.Duration,
            source,
            networkInfo);
    }

    /// <summary>Legacy path-based factory; prefer the <see cref="AudioSource"/> overload.</summary>
    public static TrackDecodePipeline Create(
        string path,
        IAudioDecoder decoder,
        AudioEngineOptions options,
        INetworkTransportInfo? networkInfo = null)
    {
        var capacity = Math.Max(
            decoder.Format.ChannelCount * 256,
            (int)Math.Ceiling(options.BufferDuration.TotalSeconds * decoder.Format.SamplesPerSecond));
        var ring = new PcmRingBuffer(capacity);
        return new TrackDecodePipeline(path, decoder, ring, decoder.Duration, source: null, networkInfo);
    }

    public void StartPump(
        IReadOnlyList<IAudioProcessor> processors,
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
            onSamplesWritten,
            onError,
            onStillAliveAfterCancel: () =>
            {
                try
                {
                    _onDecodeWorkerAliveAfterStop?.Invoke();
                }
                catch
                {
                    // diagnostics must not throw
                }
            });
        Volatile.Write(ref _observedPump, _pump);
        Volatile.Write(ref _lastStopTimedOut, 0);
        _pump.Start();
    }

    /// <summary>
    /// Stops the decode pump. May block up to two seconds waiting for the worker.
    /// Callers must not hold the player command gate while invoking this.
    /// </summary>
    public void StopPump()
    {
        StopPumpAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously stops the decode pump. Callers must not hold the player command gate.
    /// </summary>
    public async Task StopPumpAsync()
    {
        var pump = _pump;
        _pump = null;
        if (pump is null)
        {
            // Prior stop may have left an orphan; do not clear LastStopTimedOut here.
            return;
        }

        try
        {
            await pump.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (pump.StopTimedOut || pump.IsRunning)
            {
                // Timeout alone is not proof of exit — IsRunning may still be true.
                // Seek/Reset/Dispose that proceeds to Decoder.Seek/Dispose while this is true
                // races a worker that may still be inside Decoder.Read.
                Volatile.Write(ref _lastStopTimedOut, 1);
                Interlocked.Increment(ref _stopPumpTimeoutCount);
                Volatile.Write(ref _observedPump, pump);
            }
            else
            {
                Volatile.Write(ref _lastStopTimedOut, 0);
                if (ReferenceEquals(Volatile.Read(ref _observedPump), pump))
                {
                    Volatile.Write(ref _observedPump, null);
                }
            }
        }
        finally
        {
            // Dispose cancels the CTS only; it does not join a timed-out worker.
            pump.Dispose();
        }
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
        // Prefer DisposeAsync when available; sync path stops the pump without holding player locks.
        StopPump();
        Ring.Dispose();
        Decoder.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopPumpAsync().ConfigureAwait(false);
        Ring.Dispose();
        Decoder.Dispose();
    }
}
