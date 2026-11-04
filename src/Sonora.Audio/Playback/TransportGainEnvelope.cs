using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Sample-frame transport gain smoother applied after master volume and the effect rack.
/// Control threads request fade-in/out from the current gain; the audio callback advances the
/// envelope without allocations. Waits are bounded and do not require holding the player gate.
/// </summary>
internal sealed class TransportGainEnvelope
{
    /// <summary>Fixed smoothing window for pause/resume/seek edges.</summary>
    public const float FadeMs = 8f;

    private readonly int _channels;
    private readonly float _step;

    // Gain stored as scaled integer for Interlocked updates without torn floats.
    private const int GainScale = 1_000_000;
    private int _gainScaled = GainScale;
    private int _targetScaled = GainScale;
    private int _epoch;
    private readonly ManualResetEventSlim _atTarget = new(initialState: true);

    public TransportGainEnvelope(AudioFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(format.SampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(format.ChannelCount, 0);
        _channels = format.ChannelCount;
        var fadeFrames = Math.Max(1, (int)Math.Ceiling(FadeMs * 0.001 * format.SampleRate));
        _step = 1f / fadeFrames;
    }

    /// <summary>Current linear gain in [0, 1].</summary>
    public float CurrentGain => Volatile.Read(ref _gainScaled) / (float)GainScale;

    /// <summary>True when gain has reached the last requested target.</summary>
    public bool IsAtTarget
    {
        get
        {
            var gain = Volatile.Read(ref _gainScaled);
            var target = Volatile.Read(ref _targetScaled);
            return gain == target;
        }
    }

    /// <summary>Request a fade toward silence starting from the current gain.</summary>
    public int RequestFadeOut() => RequestTarget(0);

    /// <summary>Request a fade toward unity starting from the current gain.</summary>
    public int RequestFadeIn() => RequestTarget(GainScale);

    /// <summary>
    /// Immediately sets gain and target (Stop/Load/Dispose/barrier commit). Bumps the wait epoch
    /// so in-flight waiters can observe a new command.
    /// </summary>
    public void Snap(float gain)
    {
        gain = Math.Clamp(gain, 0f, 1f);
        var scaled = (int)Math.Round(gain * GainScale);
        Interlocked.Exchange(ref _gainScaled, scaled);
        Interlocked.Exchange(ref _targetScaled, scaled);
        Interlocked.Increment(ref _epoch);
        _atTarget.Set();
    }

    /// <summary>
    /// Waits until fade-out reaches silence, or the request is superseded / times out.
    /// Returns <see langword="false"/> if redirected to another target or timed out.
    /// </summary>
    public bool TryWaitUntilFadeOutComplete(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + Math.Max(1, (long)timeout.TotalMilliseconds);
        while (Environment.TickCount64 < deadline)
        {
            if (Volatile.Read(ref _targetScaled) != 0)
            {
                return false;
            }

            if (Volatile.Read(ref _gainScaled) == 0)
            {
                return true;
            }

            var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            _atTarget.Wait(remaining);
        }

        return Volatile.Read(ref _gainScaled) == 0 && Volatile.Read(ref _targetScaled) == 0;
    }

    /// <summary>
    /// Waits until fade-in reaches unity, or the request is superseded / times out.
    /// </summary>
    public bool TryWaitUntilFadeInComplete(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + Math.Max(1, (long)timeout.TotalMilliseconds);
        while (Environment.TickCount64 < deadline)
        {
            if (Volatile.Read(ref _targetScaled) != GainScale)
            {
                return false;
            }

            if (Volatile.Read(ref _gainScaled) == GainScale)
            {
                return true;
            }

            var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            _atTarget.Wait(remaining);
        }

        return Volatile.Read(ref _gainScaled) == GainScale && Volatile.Read(ref _targetScaled) == GainScale;
    }

    /// <summary>
    /// Applies the envelope to interleaved PCM in place. Advances one gain step per audio frame.
    /// Must run on the pull/callback path only.
    /// </summary>
    public void Apply(Span<float> buffer)
    {
        if (buffer.IsEmpty || _channels <= 0)
        {
            return;
        }

        var frames = buffer.Length / _channels;
        if (frames <= 0)
        {
            return;
        }

        var gainScaled = Volatile.Read(ref _gainScaled);
        var targetScaled = Volatile.Read(ref _targetScaled);
        var stepScaled = Math.Max(1, (int)Math.Round(_step * GainScale));

        var offset = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            if (gainScaled < targetScaled)
            {
                gainScaled = Math.Min(targetScaled, gainScaled + stepScaled);
            }
            else if (gainScaled > targetScaled)
            {
                gainScaled = Math.Max(targetScaled, gainScaled - stepScaled);
            }

            var gain = gainScaled / (float)GainScale;
            if (gain <= 0f)
            {
                buffer.Slice(offset, _channels).Clear();
            }
            else if (Math.Abs(gain - 1f) >= 0.0001f)
            {
                for (var c = 0; c < _channels; c++)
                {
                    buffer[offset + c] *= gain;
                }
            }

            offset += _channels;
        }

        Volatile.Write(ref _gainScaled, gainScaled);
        if (gainScaled == targetScaled)
        {
            _atTarget.Set();
        }
    }

    private int RequestTarget(int targetScaled)
    {
        Interlocked.Exchange(ref _targetScaled, targetScaled);
        Interlocked.Increment(ref _epoch);
        if (Volatile.Read(ref _gainScaled) == targetScaled)
        {
            _atTarget.Set();
        }
        else
        {
            _atTarget.Reset();
        }

        return Volatile.Read(ref _epoch);
    }
}
