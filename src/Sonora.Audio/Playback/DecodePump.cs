using Sonora.Audio.Abstractions;
using Sonora.Audio.Buffers;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Background worker that decodes PCM into a ring buffer. Never runs on the audio output callback.
/// Master volume is applied on the output pull path (<see cref="PcmVolume"/>), not here, so volume
/// changes are not delayed by ring buffering.
/// </summary>
/// <remarks>
/// Stop timeout bounds how long a caller waits; it does not mean the worker exited.
/// <see cref="ShutdownCompleted"/> is the authoritative signal that the worker has finished and
/// the decoder is no longer accessed by this pump.
/// </remarks>
internal sealed class DecodePump : IDisposable
{
    private readonly IAudioDecoder _decoder;
    private readonly PcmRingBuffer _ring;
    private readonly IReadOnlyList<IAudioProcessor> _processors;
    private readonly Action<int> _onSamplesWritten;
    private readonly Action<Exception>? _onError;
    private readonly float[] _scratch;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _started;
    private int _stopTimedOut;
    private int _stopRequested;
    private Action? _onStillAliveAfterCancel;
    private readonly TaskCompletionSource _shutdownTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _sharedStopTask;

    public DecodePump(
        IAudioDecoder decoder,
        PcmRingBuffer ring,
        IReadOnlyList<IAudioProcessor> processors,
        Action<int> onSamplesWritten,
        Action<Exception>? onError = null,
        int scratchSamples = 4096,
        Action? onStillAliveAfterCancel = null)
    {
        _decoder = decoder;
        _ring = ring;
        _processors = processors;
        _onSamplesWritten = onSamplesWritten;
        _onError = onError;
        _scratch = new float[Math.Max(256, scratchSamples)];
        _onStillAliveAfterCancel = onStillAliveAfterCancel;
    }

    /// <summary>True while the worker task has not completed.</summary>
    public bool IsRunning => _task is { IsCompleted: false };

    /// <summary>True when a caller waited out the stop join timeout while the worker was still alive.</summary>
    public bool StopTimedOut => Volatile.Read(ref _stopTimedOut) != 0;

    /// <summary>True after <see cref="StopAsync"/> has been requested (or Dispose cancelled the CTS).</summary>
    public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;

    /// <summary>
    /// Completes when the decode worker has exited (authoritative safety for Seek/Dispose/restart).
    /// Already completed if the pump never started.
    /// </summary>
    public Task ShutdownCompleted => _shutdownTcs.Task;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("Decode pump already started.");
        }

        if (_shutdownTcs.Task.IsCompleted)
        {
            throw new InvalidOperationException("Decode pump has already shut down and cannot be restarted.");
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = Task.Factory.StartNew(
            () => Run(token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Observe completion off the worker thread so cleanup cannot re-enter the worker.
        _ = _task.ContinueWith(
            static (t, state) =>
            {
                var self = (DecodePump)state!;
                if (t.IsFaulted && t.Exception is not null)
                {
                    self._shutdownTcs.TrySetException(t.Exception.InnerExceptions);
                }
                else
                {
                    self._shutdownTcs.TrySetResult();
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Cancels the worker and clears the ring without waiting. Returns <see cref="ShutdownCompleted"/>.
    /// Safe to call multiple times; concurrent callers share one stop.
    /// </summary>
    public Task RequestStop()
    {
        var existing = Volatile.Read(ref _sharedStopTask);
        if (existing is not null)
        {
            return existing;
        }

        var created = BeginStop();
        var prior = Interlocked.CompareExchange(ref _sharedStopTask, created, null);
        return prior ?? created;
    }

    /// <summary>
    /// Requests stop and waits up to <paramref name="timeout"/> for worker exit.
    /// Timeout does not mean the worker exited — await <see cref="ShutdownCompleted"/> for that.
    /// Concurrent callers share the same stop operation.
    /// </summary>
    public Task StopAsync(TimeSpan timeout) => WaitSharedStopAsync(RequestStop(), timeout);

    private Task BeginStop()
    {
        Volatile.Write(ref _stopRequested, 1);

        var cts = _cts;
        var task = _task;
        if (cts is null || task is null)
        {
            _shutdownTcs.TrySetResult();
            return Task.CompletedTask;
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
        try
        {
            _ring.Clear();
        }
        catch (ObjectDisposedException)
        {
            // ring may already be tearing down via deferred dispose
        }

        // Full stop completes when the worker exits — not when a caller timeout elapses.
        return ShutdownCompleted;
    }

    private async Task WaitSharedStopAsync(Task sharedStop, TimeSpan timeout)
    {
        if (sharedStop.IsCompleted)
        {
            await sharedStop.ConfigureAwait(false);
            Volatile.Write(ref _stopTimedOut, 0);
            return;
        }

        var completed = await Task.WhenAny(sharedStop, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != sharedStop)
        {
            // Caller wait bound elapsed; worker may still be inside Decoder.Read.
            Volatile.Write(ref _stopTimedOut, 1);
            try
            {
                _onStillAliveAfterCancel?.Invoke();
            }
            catch
            {
                // diagnostics must not throw
            }

            return;
        }

        Volatile.Write(ref _stopTimedOut, 0);
        await sharedStop.ConfigureAwait(false);
    }

    public void Dispose()
    {
        Volatile.Write(ref _stopRequested, 1);
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        try
        {
            _ring.Clear();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        _cts?.Dispose();
        _cts = null;

        if (_task is null)
        {
            _shutdownTcs.TrySetResult();
        }
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
}
