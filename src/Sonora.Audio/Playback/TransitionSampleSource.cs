using Sonora.Audio.Abstractions;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Pulls from the active ring and, when it drains at EOF with a ready next pipeline,
/// promotes next and continues filling the same destination (spanning A|B).
/// Schedules transition notifications off the calling (audio) thread.
/// </summary>
internal sealed class TransitionSampleSource : IPcmSampleSource
{
    private readonly AudioFormat _format;
    private readonly AudioEffectRack _effects;
    private readonly Action<int> _onSamplesConsumed;
    private readonly Action _onUnderrun;
    private readonly Func<bool> _isPlaying;
    private readonly Func<TrackDecodePipeline?> _getActive;
    private readonly Func<PromoteResult?> _tryPromoteNext;
    private readonly Func<bool> _hasPendingNext;
    private readonly Action<TrackTransitionedEventArgs> _scheduleTransitioned;
    private int _endOfStream;
    private int _effectsBarrier;
    private int _callbacksInRead;

    public TransitionSampleSource(
        AudioFormat format,
        AudioEffectRack effects,
        Action<int> onSamplesConsumed,
        Action onUnderrun,
        Func<bool> isPlaying,
        Func<TrackDecodePipeline?> getActive,
        Func<PromoteResult?> tryPromoteNext,
        Func<bool> hasPendingNext,
        Action<TrackTransitionedEventArgs> scheduleTransitioned)
    {
        _format = format;
        _effects = effects;
        _onSamplesConsumed = onSamplesConsumed;
        _onUnderrun = onUnderrun;
        _isPlaying = isPlaying;
        _getActive = getActive;
        _tryPromoteNext = tryPromoteNext;
        _hasPendingNext = hasPendingNext;
        _scheduleTransitioned = scheduleTransitioned;
    }

    public AudioFormat Format => _format;

    public bool IsEndOfStream => Volatile.Read(ref _endOfStream) == 1;

    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        Interlocked.Increment(ref _callbacksInRead);
        try
        {
            if (Volatile.Read(ref _effectsBarrier) == 1)
            {
                // Keep the device fed with silence while seek resets DSP / pipeline state.
                destination.Clear();
                return destination.Length;
            }

            if (Volatile.Read(ref _endOfStream) == 1)
            {
                destination.Clear();
                return 0;
            }

            var filled = 0;
            while (filled < destination.Length)
            {
                if (Volatile.Read(ref _effectsBarrier) == 1)
                {
                    destination[filled..].Clear();
                    filled = destination.Length;
                    break;
                }

                var active = _getActive();
                if (active is null)
                {
                    Interlocked.Exchange(ref _endOfStream, 1);
                    break;
                }

                var ring = active.Ring;
                var read = ring.TryRead(destination[filled..]);
                if (read > 0)
                {
                    filled += read;
                    _onSamplesConsumed(read);
                }

                if (filled == destination.Length)
                {
                    break;
                }

                if (!ring.IsCompleted)
                {
                    if (_isPlaying())
                    {
                        _onUnderrun();
                    }

                    destination[filled..].Clear();
                    filled = destination.Length;
                    break;
                }

                // Active completed and drained — try gapless promote into the same buffer.
                var promote = _tryPromoteNext();
                if (promote is not null)
                {
                    _scheduleTransitioned(promote.EventArgs);
                    continue;
                }

                if (_hasPendingNext())
                {
                    // Next is Loading/Ready but promote could not run (lock contention) — keep WASAPI alive.
                    if (_isPlaying())
                    {
                        _onUnderrun();
                    }

                    destination[filled..].Clear();
                    filled = destination.Length;
                    break;
                }

                Interlocked.Exchange(ref _endOfStream, 1);
                break;
            }

            if (filled > 0)
            {
                // Re-check barrier so Reset cannot race an in-flight Process call.
                if (Volatile.Read(ref _effectsBarrier) == 1)
                {
                    destination.Clear();
                    return destination.Length;
                }

                _effects.Process(destination[..filled], _format);
            }

            if (filled < destination.Length && Volatile.Read(ref _endOfStream) == 1)
            {
                destination[filled..].Clear();
            }

            return filled;
        }
        finally
        {
            Interlocked.Decrement(ref _callbacksInRead);
        }
    }

    public void ResetEndOfStream()
    {
        Interlocked.Exchange(ref _endOfStream, 0);
    }

    /// <summary>
    /// Blocks output callbacks from observing effect state. Waits for any in-flight
    /// <see cref="Read"/> to finish so <see cref="AudioEffectRack.ResetProcessingState"/> is safe.
    /// Does not shut down WASAPI.
    /// </summary>
    public void EnterEffectsBarrier(TimeSpan timeout)
    {
        Volatile.Write(ref _effectsBarrier, 1);
        var deadline = Environment.TickCount64 + (long)Math.Max(1, timeout.TotalMilliseconds);
        while (Volatile.Read(ref _callbacksInRead) != 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                break;
            }

            Thread.SpinWait(40);
        }
    }

    /// <summary>Allows output callbacks to process effects again.</summary>
    public void ExitEffectsBarrier()
    {
        Volatile.Write(ref _effectsBarrier, 0);
    }

    internal sealed class PromoteResult
    {
        public PromoteResult(TrackTransitionedEventArgs eventArgs)
        {
            EventArgs = eventArgs;
        }

        public TrackTransitionedEventArgs EventArgs { get; }
    }
}
