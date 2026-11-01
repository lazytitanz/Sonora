using Sonora.Audio.Abstractions;
using Sonora.Audio.Buffers;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Background worker that decodes PCM into a ring buffer. Never runs on the audio output callback.
/// </summary>
internal sealed class DecodePump : IDisposable
{
    private readonly IAudioDecoder _decoder;
    private readonly PcmRingBuffer _ring;
    private readonly IReadOnlyList<IAudioProcessor> _processors;
    private readonly Func<float> _getVolume;
    private readonly Action<int> _onSamplesWritten;
    private readonly Action<Exception>? _onError;
    private readonly float[] _scratch;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _started;

    public DecodePump(
        IAudioDecoder decoder,
        PcmRingBuffer ring,
        IReadOnlyList<IAudioProcessor> processors,
        Func<float> getVolume,
        Action<int> onSamplesWritten,
        Action<Exception>? onError = null,
        int scratchSamples = 4096)
    {
        _decoder = decoder;
        _ring = ring;
        _processors = processors;
        _getVolume = getVolume;
        _onSamplesWritten = onSamplesWritten;
        _onError = onError;
        _scratch = new float[Math.Max(256, scratchSamples)];
    }

    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("Decode pump already started.");
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = Task.Factory.StartNew(
            () => Run(token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        var cts = _cts;
        var task = _task;
        if (cts is null || task is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        // Unblock a producer waiting on a full ring.
        _ring.Clear();

        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            // Best effort; Dispose will abandon the worker.
        }

        Interlocked.Exchange(ref _started, 0);
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        _cts?.Dispose();
        _cts = null;
    }

    private void Run(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = _decoder.Read(_scratch);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _onError?.Invoke(ex);
                    break;
                }

                if (read <= 0)
                {
                    _ring.MarkCompleted();
                    break;
                }

                var slice = _scratch.AsSpan(0, read);
                var format = _decoder.Format;
                foreach (var processor in _processors)
                {
                    processor.Process(slice, format);
                }

                ApplyVolume(slice, _getVolume());

                var written = _ring.Write(slice, cancellationToken);
                if (written > 0)
                {
                    _onSamplesWritten(written);
                }

                if (written < read)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop/seek/dispose
        }
        catch (ObjectDisposedException)
        {
            // ring disposed during shutdown
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }

    private static void ApplyVolume(Span<float> buffer, float volume)
    {
        if (Math.Abs(volume - 1f) < 0.0001f)
        {
            return;
        }

        if (volume <= 0f)
        {
            buffer.Clear();
            return;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= volume;
        }
    }
}
