using Sonora.Audio.Formats;

namespace Sonora.Audio.DSP;

/// <summary>
/// Per-player effect rack with immutable processing snapshots and deferred effect disposal.
/// </summary>
internal sealed class AudioEffectRack : IAudioEffectRack, IDisposable
{
    private readonly IAudioEffectFactory? _factory;
    private readonly object _sync = new();
    private readonly List<IAudioEffectRuntime> _effects = new();
    private readonly List<IAudioEffectRuntime> _retired = new();
    private EffectSnapshot _snapshot = EffectSnapshot.Empty;
    private AudioFormat? _configuredFormat;
    private bool _bypass;
    private bool _disposed;

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
    /// updates at the block boundary, then runs each enabled effect.
    /// </summary>
    public void Process(Span<float> buffer, in AudioFormat format)
    {
        if (buffer.IsEmpty || Bypass)
        {
            return;
        }

        var snapshot = Volatile.Read(ref _snapshot);
        foreach (var effect in snapshot.Effects)
        {
            // Enabled maps to backend Bypass inside ApplyPendingUpdates (click-free where supported).
            effect.ApplyPendingUpdates();
            effect.Process(buffer, format);
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
            }

            _effects.Add(effect);
            PublishSnapshot_NoLock();
            return effect;
        }
    }

    private void PublishSnapshot_NoLock()
    {
        Volatile.Write(ref _snapshot, new EffectSnapshot(_effects.ToArray()));
    }

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
