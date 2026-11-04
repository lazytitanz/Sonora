using Sonora.Audio.Formats;

namespace Sonora.Audio.DSP;

/// <summary>
/// Per-player effect rack with immutable processing snapshots and deferred effect disposal.
/// </summary>
/// <remarks>
/// Rack <see cref="Bypass"/> keeps feeding effects (warm path) while crossfading to
/// latency-aligned dry PCM so toggles remain click-free when look-ahead effects are present.
/// </remarks>
internal sealed class AudioEffectRack : IAudioEffectRack, IDisposable
{
    /// <summary>Hard upper bound on the sum of effect <see cref="IAudioEffectRuntime.MaxLatencySamples"/>.</summary>
    public const float MaxSupportedChainLatencyMs = 100f;

    /// <summary>Bypass / latency-retarget linear crossfade duration.</summary>
    public const float CrossfadeMs = 8f;

    /// <summary>Max frames processed per inner chunk (avoids RT scratch growth).</summary>
    public const int MaxProcessFrames = 4096;

    private readonly IAudioEffectFactory? _factory;
    private readonly object _sync = new();
    private readonly List<IAudioEffectRuntime> _effects = new();
    private readonly List<IAudioEffectRuntime> _retired = new();
    private EffectSnapshot _snapshot = EffectSnapshot.Empty;
    private AudioFormat? _configuredFormat;
    private bool _bypass;
    private bool _disposed;

    // Preallocated on the control thread under _sync; Process only mutates contents.
    private float[] _inputScratch = Array.Empty<float>();
    private float[] _delayLine = Array.Empty<float>();
    private int _channels;
    private int _sampleRate;
    private int _delayCapacityFrames;
    private int _delayWriteFrame;
    private long _delayFramesWritten;

    // Audio-thread-owned mix / tap state (snapped from Configure/Reset under barrier).
    private float _bypassMix;
    private int _latencyTapA;
    private int _latencyTapB;
    private float _latencyMix = 1f;

    public AudioEffectRack(IAudioEffectFactory? factory)
    {
        _factory = factory;
    }

    public bool Bypass
    {
        get => Volatile.Read(ref _bypass);
        set => Volatile.Write(ref _bypass, value);
    }

    public IReadOnlyList<IAudioEffect> Items
    {
        get
        {
            lock (_sync)
            {
                return _effects.ToArray();
            }
        }
    }

    public IEqualizerEffect AddEqualizer(EqualizerOptions? options = null)
    {
        var factory = RequireFactory();
        var effect = factory.CreateEqualizer(options ?? EqualizerOptions.CreateSixBand());
        return (IEqualizerEffect)AddRuntime((IAudioEffectRuntime)effect);
    }

    public IPreampEffect AddPreamp(float gainDb = 0f)
    {
        var factory = RequireFactory();
        var effect = factory.CreatePreamp(gainDb);
        return (IPreampEffect)AddRuntime((IAudioEffectRuntime)effect);
    }

    public ILimiterEffect AddLimiter(LimiterOptions? options = null)
    {
        var factory = RequireFactory();
        var effect = factory.CreateLimiter(options ?? new LimiterOptions());
        return (ILimiterEffect)AddRuntime((IAudioEffectRuntime)effect);
    }

    public bool Remove(IAudioEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (effect is not IAudioEffectRuntime runtime)
        {
            return false;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_effects.Remove(runtime))
            {
                return false;
            }

            PublishSnapshot_NoLock();
            _retired.Add(runtime);
            return true;
        }
    }

    public void Move(int fromIndex, int toIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (fromIndex < 0 || fromIndex >= _effects.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(fromIndex));
            }

            if (toIndex < 0 || toIndex >= _effects.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(toIndex));
            }

            if (fromIndex == toIndex)
            {
                return;
            }

            var item = _effects[fromIndex];
            _effects.RemoveAt(fromIndex);
            _effects.Insert(toIndex, item);
            PublishSnapshot_NoLock();
        }
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_effects.Count == 0)
            {
                return;
            }

            _retired.AddRange(_effects);
            _effects.Clear();
            PublishSnapshot_NoLock();
        }
    }

    /// <summary>
    /// Processes the active snapshot. Safe for the audio callback: applies pending parameter
    /// updates at the block boundary, runs each effect (warm path), then crossfades to
    /// latency-aligned dry when bypassed.
    /// </summary>
    public void Process(Span<float> buffer, in AudioFormat format)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var channels = format.ChannelCount;
        if (channels <= 0 || buffer.Length % channels != 0)
        {
            return;
        }

        var totalFrames = buffer.Length / channels;
        var sampleRate = format.SampleRate > 0 ? format.SampleRate : _sampleRate;
        if (sampleRate <= 0)
        {
            sampleRate = 48_000;
        }

        var scratch = _inputScratch;
        var delay = _delayLine;
        var delayCap = Volatile.Read(ref _delayCapacityFrames);
        var configuredChannels = Volatile.Read(ref _channels);
        var buffersReady = configuredChannels == channels
            && scratch.Length >= channels
            && delayCap > 0
            && delay.Length >= delayCap * channels;

        if (!buffersReady)
        {
            // Before Configure: wet-only when not bypassed; dry pass-through when bypassed.
            if (Bypass)
            {
                return;
            }

            var early = Volatile.Read(ref _snapshot);
            foreach (var effect in early.Effects)
            {
                effect.ApplyPendingUpdates();
                effect.Process(buffer, format);
            }

            return;
        }

        var bypassTarget = Bypass ? 1f : 0f;
        var slew = 1f / Math.Max(1f, CrossfadeMs * 0.001f * sampleRate);

        var snapshot = Volatile.Read(ref _snapshot);
        foreach (var effect in snapshot.Effects)
        {
            effect.ApplyPendingUpdates();
        }

        var effectiveLatency = 0;
        foreach (var effect in snapshot.Effects)
        {
            effectiveLatency += effect.LatencySamples;
        }

        if (effectiveLatency > delayCap)
        {
            effectiveLatency = delayCap;
        }

        if (effectiveLatency != _latencyTapB)
        {
            if (_latencyMix >= 1f)
            {
                _latencyTapA = _latencyTapB;
                _latencyTapB = effectiveLatency;
                _latencyMix = 0f;
            }
            else
            {
                _latencyTapB = effectiveLatency;
            }
        }

        var maxChunkFrames = Math.Max(1, Math.Min(MaxProcessFrames, scratch.Length / channels));

        for (var frameOffset = 0; frameOffset < totalFrames;)
        {
            var chunkFrames = Math.Min(maxChunkFrames, totalFrames - frameOffset);
            var chunk = buffer.Slice(frameOffset * channels, chunkFrames * channels);
            var dryChunk = scratch.AsSpan(0, chunk.Length);
            chunk.CopyTo(dryChunk);

            foreach (var effect in snapshot.Effects)
            {
                effect.Process(chunk, format);
            }

            for (var f = 0; f < chunkFrames; f++)
            {
                // Write current dry frame into the delay line, then read taps relative to this write.
                WriteDelayFrame(dryChunk, f, channels, delay, delayCap);

                if (_bypassMix < bypassTarget)
                {
                    _bypassMix = Math.Min(bypassTarget, _bypassMix + slew);
                }
                else if (_bypassMix > bypassTarget)
                {
                    _bypassMix = Math.Max(bypassTarget, _bypassMix - slew);
                }

                if (_latencyMix < 1f)
                {
                    _latencyMix = Math.Min(1f, _latencyMix + slew);
                }

                var g = _bypassMix;
                var ell = _latencyMix;
                var baseIndex = f * channels;

                for (var ch = 0; ch < channels; ch++)
                {
                    var dryA = ReadDelayBehind(ch, _latencyTapA, channels, delay, delayCap);
                    var dryB = ReadDelayBehind(ch, _latencyTapB, channels, delay, delayCap);
                    var alignedDry = dryA * (1f - ell) + dryB * ell;
                    var wet = chunk[baseIndex + ch];
                    chunk[baseIndex + ch] = alignedDry * g + wet * (1f - g);
                }
            }

            frameOffset += chunkFrames;
        }

        if (_latencyMix >= 1f)
        {
            _latencyTapA = _latencyTapB;
        }
    }

    /// <summary>
    /// Configures all live effects for the session format. Preserves user parameters across tracks.
    /// </summary>
    public void Configure(in AudioFormat format)
    {
        lock (_sync)
        {
            _configuredFormat = format;
            foreach (var effect in _effects)
            {
                effect.Configure(format);
            }

            EnsureBuffers_NoLock(format);
            ValidateChainLatency_NoLock();
            ClearDelayAndSnapMix_NoLock();
        }
    }

    /// <summary>Resets processing state (seek / stop) without changing user parameters.</summary>
    public void ResetProcessingState()
    {
        lock (_sync)
        {
            foreach (var effect in _effects)
            {
                effect.Reset();
            }

            ClearDelayAndSnapMix_NoLock();
        }
    }

    /// <summary>
    /// Disposes effects that were removed from the chain. Call only when the audio callback
    /// cannot still be using a retired instance (output paused/stopped, seek, teardown, dispose).
    /// </summary>
    public void DisposeRetired()
    {
        IAudioEffectRuntime[] toDispose;
        lock (_sync)
        {
            if (_retired.Count == 0)
            {
                return;
            }

            toDispose = _retired.ToArray();
            _retired.Clear();
        }

        foreach (var effect in toDispose)
        {
            effect.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _retired.AddRange(_effects);
            _effects.Clear();
            Volatile.Write(ref _snapshot, EffectSnapshot.Empty);
            _inputScratch = Array.Empty<float>();
            _delayLine = Array.Empty<float>();
            Volatile.Write(ref _delayCapacityFrames, 0);
            Volatile.Write(ref _channels, 0);
        }

        DisposeRetired();
    }

    private IAudioEffectRuntime AddRuntime(IAudioEffectRuntime effect)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_configuredFormat is { } format)
            {
                effect.Configure(format);
                EnsureBuffers_NoLock(format);
            }

            _effects.Add(effect);
            ValidateChainLatency_NoLock();
            PublishSnapshot_NoLock();
            return effect;
        }
    }

    private void PublishSnapshot_NoLock()
    {
        Volatile.Write(ref _snapshot, new EffectSnapshot(_effects.ToArray()));
    }

    private void EnsureBuffers_NoLock(in AudioFormat format)
    {
        Volatile.Write(ref _channels, format.ChannelCount);
        _sampleRate = format.SampleRate;
        var maxLatencyFrames = MaxSupportedLatencyFrames(format.SampleRate);

        var scratchSamples = MaxProcessFrames * format.ChannelCount;
        if (_inputScratch.Length < scratchSamples)
        {
            _inputScratch = new float[scratchSamples];
        }

        // Capacity at the rack maximum so Add during playback never reallocates.
        var delaySamples = Math.Max(format.ChannelCount, maxLatencyFrames * format.ChannelCount);
        if (_delayLine.Length < delaySamples)
        {
            _delayLine = new float[delaySamples];
        }

        Volatile.Write(ref _delayCapacityFrames, maxLatencyFrames);
    }

    private void ValidateChainLatency_NoLock()
    {
        if (_configuredFormat is not { } format || format.SampleRate <= 0)
        {
            return;
        }

        var sum = 0;
        foreach (var effect in _effects)
        {
            sum += effect.MaxLatencySamples;
        }

        var maxFrames = MaxSupportedLatencyFrames(format.SampleRate);
        if (sum > maxFrames)
        {
            throw new InvalidOperationException(
                $"Effect chain max latency is {sum} frames, which exceeds the rack limit of {maxFrames} frames " +
                $"({MaxSupportedChainLatencyMs} ms at {format.SampleRate} Hz).");
        }
    }

    private void ClearDelayAndSnapMix_NoLock()
    {
        Array.Clear(_delayLine);
        _delayWriteFrame = 0;
        _delayFramesWritten = 0;
        _bypassMix = Bypass ? 1f : 0f;
        _latencyTapA = 0;
        _latencyTapB = 0;
        _latencyMix = 1f;
    }

    private void WriteDelayFrame(ReadOnlySpan<float> dryChunk, int frame, int channels, float[] delay, int capacityFrames)
    {
        var dest = _delayWriteFrame * channels;
        var src = frame * channels;
        for (var ch = 0; ch < channels; ch++)
        {
            delay[dest + ch] = dryChunk[src + ch];
        }

        _delayWriteFrame++;
        if (_delayWriteFrame >= capacityFrames)
        {
            _delayWriteFrame = 0;
        }

        _delayFramesWritten++;
    }

    /// <summary>
    /// Reads a channel sample <paramref name="latencyFrames"/> behind the frame just written.
    /// Latency 0 returns that just-written sample. Missing history returns 0.
    /// </summary>
    private float ReadDelayBehind(int channel, int latencyFrames, int channels, float[] delay, int capacityFrames)
    {
        if (latencyFrames < 0)
        {
            latencyFrames = 0;
        }

        if (latencyFrames > 0 && _delayFramesWritten <= latencyFrames)
        {
            return 0f;
        }

        // Just-written frame is at (_delayWriteFrame - 1 + capacity) % capacity.
        var writtenFrame = _delayWriteFrame - 1;
        if (writtenFrame < 0)
        {
            writtenFrame += capacityFrames;
        }

        var readFrame = writtenFrame - latencyFrames;
        while (readFrame < 0)
        {
            readFrame += capacityFrames;
        }

        return delay[readFrame * channels + channel];
    }

    private static int MaxSupportedLatencyFrames(int sampleRate)
        => Math.Max(1, (int)Math.Round(MaxSupportedChainLatencyMs * sampleRate / 1000.0));

    private IAudioEffectFactory RequireFactory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_factory is null)
        {
            throw new InvalidOperationException(
                "No audio effect factory is configured. Create the engine with SonoraWindows.CreateEngine() (or supply AudioEngineOptions.EffectFactory).");
        }

        return _factory;
    }
}
