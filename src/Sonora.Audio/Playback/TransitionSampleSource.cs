using Sonora.Audio.Abstractions;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Pulls from the active ring and, when it drains at EOF with a ready next pipeline,
/// promotes next and continues filling the same destination (spanning A|B).
/// Applies master volume then the effect rack on the pull path so live volume and DSP
/// changes are not delayed by ring buffering. Schedules transition notifications off the
/// calling (audio) thread.
/// </summary>
internal sealed class TransitionSampleSource : IPcmSampleSource
{
    /// <summary>
    /// Caps consecutive empty-track promotions inside one <see cref="Read"/> to avoid
    /// infinite loops when a queue of zero-length completed rings is promoted.
    /// </summary>
    private const int MaxEmptyPromotesPerRead = 16;

    /// <summary>
    /// After promoting onto an empty completed ring, keep-alive reads before terminal EOS
    /// so hosts can BeginLoad the next track from TrackTransitioned without racing the latch.
    /// </summary>
    private const int EmptyPromoteGraceReads = 256;

    private readonly AudioFormat _format;
    private readonly AudioEffectRack _effects;
    private readonly Func<float> _getVolume;
    private readonly Action<int> _onSamplesConsumed;
    private readonly Action _onUnderrun;
    private readonly Func<bool> _isPlaying;
    private readonly Func<bool> _isOutputSwitching;
    private readonly Func<TrackDecodePipeline?> _getActive;
    private readonly Func<PromoteResult?> _tryPromoteNext;
    private readonly Func<bool> _hasPendingNext;
    private readonly Action<TrackTransitionedEventArgs> _scheduleTransitioned;
    private readonly Action<SeekSilenceReason>? _onSilence;
    private readonly Action? _onCallbackHeartbeat;
    private int _endOfStream;
    private int _effectsBarrier;
    private int _callbacksInRead;
    private int _lastReportedSilence;
    private int _emptyPromoteGraceRemaining;

    public TransitionSampleSource(
        AudioFormat format,
        AudioEffectRack effects,
        Func<float> getVolume,
        Action<int> onSamplesConsumed,
        Action onUnderrun,
        Func<bool> isPlaying,
        Func<TrackDecodePipeline?> getActive,
        Func<PromoteResult?> tryPromoteNext,
        Func<bool> hasPendingNext,
        Action<TrackTransitionedEventArgs> scheduleTransitioned,
        Func<bool>? isOutputSwitching = null,
        Action<SeekSilenceReason>? onSilence = null,
        Action? onCallbackHeartbeat = null)
    {
        _format = format;
        _effects = effects;
        _getVolume = getVolume;
        _onSamplesConsumed = onSamplesConsumed;
        _onUnderrun = onUnderrun;
        _isPlaying = isPlaying;
        _isOutputSwitching = isOutputSwitching ?? (() => false);
        _getActive = getActive;
        _tryPromoteNext = tryPromoteNext;
        _hasPendingNext = hasPendingNext;
        _scheduleTransitioned = scheduleTransitioned;
        _onSilence = onSilence;
        _onCallbackHeartbeat = onCallbackHeartbeat;
    }

    public AudioFormat Format => _format;

    /// <summary>
    /// True when terminal session EOS is latched. Active-track EOF with a pending/ready next
    /// must not remain latched — see <see cref="Read"/>.
    /// </summary>
    public bool IsEndOfStream => Volatile.Read(ref _endOfStream) == 1;

    /// <summary>1 when the effects barrier is armed.</summary>
    public int EffectsBarrier => Volatile.Read(ref _effectsBarrier);

    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        Interlocked.Increment(ref _callbacksInRead);
        try
        {
            try
            {
                _onCallbackHeartbeat?.Invoke();
            }
            catch
            {
                // diagnostics must not throw
            }

            if (Volatile.Read(ref _effectsBarrier) == 1)
            {
                ReportSilence(SeekSilenceReason.EffectsBarrier);
                destination.Clear();
                return destination.Length;
            }

            if (_isOutputSwitching())
            {
                // Feed silence without draining the ring so a detached WASAPI/fake pull thread
                // cannot advance samplesConsumed or steal primed PCM before Shutdown joins.
                ReportSilence(SeekSilenceReason.OutputSwitching);
                destination.Clear();
                return destination.Length;
            }

            // Sticky terminal EOS only when no gapless transition is still pending.
            // If next is Loading/Ready, clear the latch and continue — active-track EOF ≠ session EOS.
            if (Volatile.Read(ref _endOfStream) == 1)
            {
                if (_hasPendingNext())
                {
                    Interlocked.Exchange(ref _endOfStream, 0);
                }
                else
                {
                    ReportSilence(SeekSilenceReason.EndOfStream);
                    destination.Clear();
                    return 0;
                }
            }

            var filled = 0;
            var emptyPromotes = 0;
            while (filled < destination.Length)
            {
                if (Volatile.Read(ref _effectsBarrier) == 1)
                {
                    ReportSilence(SeekSilenceReason.EffectsBarrier);
                    destination[filled..].Clear();
                    filled = destination.Length;
                    break;
                }

                if (_isOutputSwitching())
                {
                    ReportSilence(SeekSilenceReason.OutputSwitching);
                    destination[filled..].Clear();
                    filled = destination.Length;
                    break;
                }

                var active = _getActive();
                if (active is null)
                {
                    if (_hasPendingNext())
                    {
                        ReportPendingNextKeepAlive(destination, ref filled);
                        break;
                    }

                    LatchTerminalEndOfStream();
                    break;
                }

                var ring = active.Ring;
                var read = ring.TryRead(destination[filled..]);
                if (read > 0)
                {
                    filled += read;
                    _onSamplesConsumed(read);
                    ReportSilence(SeekSilenceReason.None);
                    emptyPromotes = 0;
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

                    ReportSilence(SeekSilenceReason.Underrun);
                    destination[filled..].Clear();
                    filled = destination.Length;
                    break;
                }

                // Active completed and drained — try gapless promote into the same buffer.
                // An empty completed next is a valid (zero-length) track: promote it, then continue.
                // Do not latch terminal EOS while another next is still pending/ready.
                var promote = _tryPromoteNext();
                if (promote is not null)
                {
                    _scheduleTransitioned(promote.EventArgs);
                    emptyPromotes++;

                    // If the promoted ring is already empty+completed, grant a short keep-alive
                    // window for TrackTransitioned → PreloadNext BeginLoad before terminal EOS.
                    var promoted = _getActive();
                    if (promoted?.Ring is { IsCompleted: true, Count: 0 })
                    {
                        Volatile.Write(ref _emptyPromoteGraceRemaining, EmptyPromoteGraceReads);
                    }
                    else
                    {
                        Volatile.Write(ref _emptyPromoteGraceRemaining, 0);
                    }

                    if (emptyPromotes > MaxEmptyPromotesPerRead)
                    {
                        // Bound empty-track chains inside one callback; keep the device alive.
                        ReportPendingNextKeepAlive(destination, ref filled);
                        break;
                    }

                    continue;
                }

                if (_hasPendingNext())
                {
                    Volatile.Write(ref _emptyPromoteGraceRemaining, 0);
                    // Next is Loading/Ready but promote could not run (lock contention or still loading).
                    ReportPendingNextKeepAlive(destination, ref filled);
                    break;
                }

                var grace = Volatile.Read(ref _emptyPromoteGraceRemaining);
                if (grace > 0)
                {
                    Volatile.Write(ref _emptyPromoteGraceRemaining, grace - 1);
                    ReportPendingNextKeepAlive(destination, ref filled);
                    break;
                }

                LatchTerminalEndOfStream();
                break;
            }

            if (filled > 0)
            {
                // Re-check barrier / switch so Reset or detach cannot race an in-flight Process call.
                if (Volatile.Read(ref _effectsBarrier) == 1 || _isOutputSwitching())
                {
                    destination.Clear();
                    return destination.Length;
                }

                // Volume then DSP: preserves prior ordering (volume was pre-effects) while applying
                // after the ring so changes take effect on the next output callback.
                var wet = destination[..filled];
                PcmVolume.Apply(wet, _getVolume());
                _effects.Process(wet, _format);
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
        // Allow a fresh silence reason to be reported after recovery.
        Volatile.Write(ref _lastReportedSilence, (int)SeekSilenceReason.None);
        Volatile.Write(ref _emptyPromoteGraceRemaining, 0);
    }

    /// <summary>
    /// Arms the effects barrier so subsequent <see cref="Read"/> calls return silence and do not
    /// drain or promote. Does not wait for an in-flight callback — pair with
    /// <see cref="WaitEffectsBarrier"/> after releasing locks that an audio callback must not hold.
    /// </summary>
    public void ArmEffectsBarrier()
    {
        Volatile.Write(ref _effectsBarrier, 1);
    }

    /// <summary>
    /// Waits for any in-flight <see cref="Read"/> to finish after <see cref="ArmEffectsBarrier"/>
    /// so <see cref="AudioEffectRack.ResetProcessingState"/> is safe.
    /// </summary>
    public void WaitEffectsBarrier(TimeSpan timeout)
    {
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

    /// <summary>
    /// Blocks output callbacks from observing effect state. Waits for any in-flight
    /// <see cref="Read"/> to finish so <see cref="AudioEffectRack.ResetProcessingState"/> is safe.
    /// Does not shut down WASAPI.
    /// </summary>
    public void EnterEffectsBarrier(TimeSpan timeout)
    {
        ArmEffectsBarrier();
        WaitEffectsBarrier(timeout);
    }

    /// <summary>Allows output callbacks to process effects again.</summary>
    public void ExitEffectsBarrier()
    {
        Volatile.Write(ref _effectsBarrier, 0);
    }

    private void LatchTerminalEndOfStream()
    {
        Interlocked.Exchange(ref _endOfStream, 1);
        ReportSilence(SeekSilenceReason.EndOfStream);
    }

    private void ReportPendingNextKeepAlive(Span<float> destination, ref int filled)
    {
        // Keep WASAPI / pull loops alive with non-zero silence while gapless next is pending.
        if (_isPlaying())
        {
            _onUnderrun();
        }

        ReportSilence(SeekSilenceReason.PendingNext);
        destination[filled..].Clear();
        filled = destination.Length;
    }

    private void ReportSilence(SeekSilenceReason reason)
    {
        var prior = Volatile.Read(ref _lastReportedSilence);
        var code = (int)reason;
        if (prior == code)
        {
            return;
        }

        Volatile.Write(ref _lastReportedSilence, code);
        try
        {
            _onSilence?.Invoke(reason);
        }
        catch
        {
            // diagnostics must not throw
        }
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
