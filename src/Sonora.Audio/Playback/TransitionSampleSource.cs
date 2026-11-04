using Sonora.Audio.Abstractions;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Pulls from the active ring and, when it drains at EOF with a ready next pipeline,
/// promotes next and continues filling the same destination (spanning A|B).
/// Optional EOS-anchored crossfade overlaps the outgoing and incoming rings before
/// master volume and the effect rack. Schedules transition notifications off the
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

    /// <summary>Preallocated scratch for the incoming track during crossfade (interleaved floats).</summary>
    private const int ScratchSampleCapacity = 8192 * 2;

    /// <summary>Maximum transient B-underrun hold while freezing fade progress (session frames).</summary>
    private const int MaxCrossfadeUnderrunHoldFramesDefault = 100; // ~2.3 ms @ 44.1k; scaled below by rate

    private readonly AudioFormat _format;
    private readonly AudioEffectRack _effects;
    private readonly Func<float> _getVolume;
    private readonly Action<int> _onSamplesConsumed;
    private readonly Action _onUnderrun;
    private readonly Func<bool> _isPlaying;
    private readonly Func<bool> _isOutputSwitching;
    private readonly Func<TrackDecodePipeline?> _getActive;
    private readonly Func<TrackDecodePipeline?> _getFadingOut;
    private readonly Func<PromoteResult?> _tryPromoteNext;
    private readonly Func<CrossfadeCommit?> _tryBeginCrossfade;
    private readonly Func<TrackDecodePipeline?> _tryTakeFadingOut;
    private readonly Action<TrackDecodePipeline> _scheduleFadingOutDispose;
    private readonly Func<bool> _hasPendingNext;
    private readonly Func<TimeSpan> _getCrossfadeDuration;
    private readonly Action<TrackTransitionedEventArgs> _scheduleTransitioned;
    private readonly Action<SeekSilenceReason>? _onSilence;
    private readonly Action? _onCallbackHeartbeat;
    private readonly float[] _scratch;
    private readonly int _maxHoldFrames;

    private int _endOfStream;
    private int _effectsBarrier;
    private int _callbacksInRead;
    private int _lastReportedSilence;
    private int _emptyPromoteGraceRemaining;

    private int _crossfadeActive;
    private int _fadeFramesTotal;
    private int _fadeFramesDone;
    private int _crossfadeCurve;
    private int _crossfadeHoldFrames;
    private int _crossfadeUnderrunLatched;

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
        Action? onCallbackHeartbeat = null,
        Func<TrackDecodePipeline?>? getFadingOut = null,
        Func<CrossfadeCommit?>? tryBeginCrossfade = null,
        Func<TrackDecodePipeline?>? tryTakeFadingOut = null,
        Action<TrackDecodePipeline>? scheduleFadingOutDispose = null,
        Func<TimeSpan>? getCrossfadeDuration = null)
    {
        _format = format;
        _effects = effects;
        _getVolume = getVolume;
        _onSamplesConsumed = onSamplesConsumed;
        _onUnderrun = onUnderrun;
        _isPlaying = isPlaying;
        _isOutputSwitching = isOutputSwitching ?? (() => false);
        _getActive = getActive;
        _getFadingOut = getFadingOut ?? (() => null);
        _tryPromoteNext = tryPromoteNext;
        _tryBeginCrossfade = tryBeginCrossfade ?? (() => null);
        _tryTakeFadingOut = tryTakeFadingOut ?? (() => null);
        _scheduleFadingOutDispose = scheduleFadingOutDispose ?? (_ => { });
        _hasPendingNext = hasPendingNext;
        _getCrossfadeDuration = getCrossfadeDuration ?? (() => TimeSpan.Zero);
        _scheduleTransitioned = scheduleTransitioned;
        _onSilence = onSilence;
        _onCallbackHeartbeat = onCallbackHeartbeat;
        _scratch = new float[ScratchSampleCapacity];
        _maxHoldFrames = Math.Max(
            MaxCrossfadeUnderrunHoldFramesDefault,
            (int)Math.Ceiling(0.100 * format.SampleRate)); // 100 ms
    }

    public AudioFormat Format => _format;

    /// <summary>
    /// True when terminal session EOS is latched. Active-track EOF with a pending/ready next
    /// must not remain latched — see <see cref="Read"/>.
    /// </summary>
    public bool IsEndOfStream => Volatile.Read(ref _endOfStream) == 1;

    /// <summary>1 when the effects barrier is armed.</summary>
    public int EffectsBarrier => Volatile.Read(ref _effectsBarrier);

    /// <summary>True while an EOS-anchored crossfade is in progress.</summary>
    public bool IsCrossfadeActive => Volatile.Read(ref _crossfadeActive) == 1;

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
                if (_hasPendingNext() || Volatile.Read(ref _crossfadeActive) == 1)
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

                if (Volatile.Read(ref _crossfadeActive) == 1)
                {
                    if (!ReadCrossfade(destination, ref filled))
                    {
                        break;
                    }

                    continue;
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

                // EOS-anchored crossfade: wait until only the fade-sized tail remains, draining any
                // surplus at full level first so a large ring cannot fade the start of a short track.
                if (TryArmCrossfade(active))
                {
                    emptyPromotes = 0;
                    continue;
                }

                var crossfadeDuration = _getCrossfadeDuration();
                if (crossfadeDuration > TimeSpan.Zero
                    && ring.IsProducerCompleted
                    && ring.Count > 0)
                {
                    var channels = Math.Max(1, _format.ChannelCount);
                    var configuredFrames = CrossfadeMixer.DurationToFadeFrames(
                        crossfadeDuration,
                        _format.SampleRate);
                    var framesBuffered = ring.Count / channels;
                    if (framesBuffered > configuredFrames)
                    {
                        var fullLevelSamples = (framesBuffered - configuredFrames) * channels;
                        var limit = Math.Min(destination.Length - filled, fullLevelSamples);
                        var limited = ring.TryRead(destination.Slice(filled, limit));
                        if (limited > 0)
                        {
                            filled += limited;
                            _onSamplesConsumed(limited);
                            ReportSilence(SeekSilenceReason.None);
                            emptyPromotes = 0;
                        }

                        if (filled == destination.Length)
                        {
                            break;
                        }

                        if (TryArmCrossfade(active))
                        {
                            continue;
                        }
                    }
                }

                var read = ring.TryRead(destination[filled..]);
                if (read > 0)
                {
                    filled += read;
                    _onSamplesConsumed(read);
                    ReportSilence(SeekSilenceReason.None);
                    emptyPromotes = 0;

                    if (filled < destination.Length && TryArmCrossfade(active))
                    {
                        continue;
                    }
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
    /// Clears in-progress crossfade gain state. Does not dispose pipelines — the player must
    /// take <c>_fadingOut</c> under the pipeline lock after excluding the pull path.
    /// </summary>
    public void AbortCrossfade()
    {
        ClearCrossfadeState();
    }

    /// <summary>
    /// Arms the effects barrier so subsequent <see cref="Read"/> calls return silence and do not
    /// drain or promote. Does not wait for an in-flight callback — pair with
    /// <see cref="WaitEffectsBarrier"/> after releasing locks that an audio callback must not hold.
    /// </summary>
    public void ArmEffectsBarrier()
    {
        Volatile.Write(ref _effectsBarrier, 1);
        ClearCrossfadeState();
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

    private bool TryArmCrossfade(TrackDecodePipeline active)
    {
        var duration = _getCrossfadeDuration();
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        if (!active.Ring.IsProducerCompleted || active.Ring.Count == 0)
        {
            return false;
        }

        var channels = Math.Max(1, _format.ChannelCount);
        var framesBuffered = active.Ring.Count / channels;
        var configuredFrames = CrossfadeMixer.DurationToFadeFrames(duration, _format.SampleRate);
        // Wait until only the fade-sized (or shorter) tail remains so a large ring cannot
        // begin fading at the start of a short track that fully fit in the buffer.
        if (framesBuffered > configuredFrames)
        {
            return false;
        }

        var commit = _tryBeginCrossfade();
        if (commit is null)
        {
            return false;
        }

        _scheduleTransitioned(commit.EventArgs);
        _fadeFramesTotal = commit.FadeFrames;
        _fadeFramesDone = 0;
        _crossfadeCurve = (int)commit.Curve;
        _crossfadeHoldFrames = 0;
        _crossfadeUnderrunLatched = 0;
        Volatile.Write(ref _crossfadeActive, 1);
        Volatile.Write(ref _emptyPromoteGraceRemaining, 0);
        return true;
    }

    /// <summary>
    /// Fills from the dual-ring crossfade. Returns false when the caller should break the outer loop
    /// (underrun keepalive already padded); true to continue.
    /// </summary>
    private bool ReadCrossfade(Span<float> destination, ref int filled)
    {
        var channels = _format.ChannelCount;
        if (channels <= 0)
        {
            ClearCrossfadeState();
            return true;
        }

        var fading = _getFadingOut();
        var incoming = _getActive();
        if (fading is null || incoming is null || fading.IsDisposePending || incoming.IsDisposePending)
        {
            CompleteCrossfadeRelease();
            return true;
        }

        var framesLeft = _fadeFramesTotal - _fadeFramesDone;
        if (framesLeft <= 0)
        {
            CompleteCrossfadeRelease();
            return true;
        }

        var samplesAvailable = destination.Length - filled;
        var maxScratchFrames = Math.Max(1, _scratch.Length / channels);
        var framesWanted = Math.Min(framesLeft, samplesAvailable / channels);
        framesWanted = Math.Min(framesWanted, maxScratchFrames);
        if (framesWanted <= 0)
        {
            // Destination has a partial frame left — pad silence and stop.
            destination[filled..].Clear();
            filled = destination.Length;
            return false;
        }

        var samplesWanted = framesWanted * channels;
        var destSlice = destination.Slice(filled, samplesWanted);

        var aRead = fading.Ring.TryRead(destSlice);
        if (aRead < samplesWanted)
        {
            destSlice[aRead..].Clear();
        }

        var scratchSlice = _scratch.AsSpan(0, samplesWanted);
        var bRead = incoming.Ring.TryRead(scratchSlice);
        if (bRead < samplesWanted)
        {
            scratchSlice[bRead..].Clear();
        }

        var bTransient = bRead == 0
            && !incoming.Ring.IsProducerCompleted
            && !incoming.IsDisposePending;

        var curve = (CrossfadeCurve)_crossfadeCurve;
        if (bTransient && _crossfadeHoldFrames < _maxHoldFrames)
        {
            _crossfadeHoldFrames += framesWanted;
            if (_isPlaying() && Interlocked.Exchange(ref _crossfadeUnderrunLatched, 1) == 0)
            {
                _onUnderrun();
            }

            ReportSilence(SeekSilenceReason.Underrun);

            // Hold t: emit A at current gain only; do not invent B; do not advance fade progress.
            var tHold = _fadeFramesTotal <= 0 ? 1f : _fadeFramesDone / (float)_fadeFramesTotal;
            CrossfadeMixer.Gains(curve, tHold, out var gAHold, out _);
            CrossfadeMixer.ScaleInPlace(destSlice, gAHold);
            filled += samplesWanted;
            // Position freeze: no B PCM consumed.
            return filled < destination.Length;
        }

        _crossfadeHoldFrames = 0;
        Interlocked.Exchange(ref _crossfadeUnderrunLatched, 0);

        // Per-frame gains for continuous envelopes across callback boundaries.
        for (var frame = 0; frame < framesWanted; frame++)
        {
            var t = _fadeFramesTotal <= 0
                ? 1f
                : (_fadeFramesDone + frame) / (float)_fadeFramesTotal;
            CrossfadeMixer.Gains(curve, t, out var gA, out var gB);
            var offset = frame * channels;
            for (var c = 0; c < channels; c++)
            {
                var i = offset + c;
                destSlice[i] = (gA * destSlice[i]) + (gB * scratchSlice[i]);
            }
        }

        _fadeFramesDone += framesWanted;
        if (bRead > 0)
        {
            _onSamplesConsumed(bRead);
            ReportSilence(SeekSilenceReason.None);
        }
        else if (_isPlaying())
        {
            // Permanent B shortfall (EOS/failure) or degraded hold — still advancing t.
            _onUnderrun();
            ReportSilence(SeekSilenceReason.Underrun);
        }

        filled += samplesWanted;

        if (_fadeFramesDone >= _fadeFramesTotal)
        {
            CompleteCrossfadeRelease();
        }

        return true;
    }

    private void CompleteCrossfadeRelease()
    {
        ClearCrossfadeState();
        var releasing = _tryTakeFadingOut();
        if (releasing is not null)
        {
            _scheduleFadingOutDispose(releasing);
        }
    }

    private void ClearCrossfadeState()
    {
        Volatile.Write(ref _crossfadeActive, 0);
        _fadeFramesTotal = 0;
        _fadeFramesDone = 0;
        _crossfadeHoldFrames = 0;
        Interlocked.Exchange(ref _crossfadeUnderrunLatched, 0);
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

    /// <summary>Result of committing an EOS-anchored crossfade promote.</summary>
    internal sealed class CrossfadeCommit
    {
        public CrossfadeCommit(TrackTransitionedEventArgs eventArgs, int fadeFrames, CrossfadeCurve curve)
        {
            EventArgs = eventArgs;
            FadeFrames = fadeFrames;
            Curve = curve;
        }

        public TrackTransitionedEventArgs EventArgs { get; }
        public int FadeFrames { get; }
        public CrossfadeCurve Curve { get; }
    }
}
