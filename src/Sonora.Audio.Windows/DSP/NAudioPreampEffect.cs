using NAudio.Effects;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Windows.DSP;

/// <summary>Preamp wrapping NAudio <see cref="GainEffect"/>.</summary>
internal sealed class NAudioPreampEffect : IPreampEffect, IAudioEffectRuntime
{
    private readonly GainEffect _effect = new();
    private float _gainDb;
    private float _pendingGainDb;
    private int _enabled = 1;
    private int _pending;
    private bool _configured;
    private bool _disposed;

    public NAudioPreampEffect(float gainDb)
    {
        _gainDb = gainDb;
        _pendingGainDb = gainDb;
        _effect.GainDb = gainDb;
        Interlocked.Exchange(ref _pending, 1);
    }

    public string Name => "Preamp";

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) == 1;
        set
        {
            Volatile.Write(ref _enabled, value ? 1 : 0);
            Interlocked.Exchange(ref _pending, 1);
        }
    }

    public float GainDb
    {
        get => Volatile.Read(ref _gainDb);
        set
        {
            Volatile.Write(ref _pendingGainDb, value);
            Interlocked.Exchange(ref _pending, 1);
        }
    }

    public void Configure(in AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _effect.Configure(NAudioEffectHelpers.ToIeeeFloatWaveFormat(format));
        _configured = true;
        Interlocked.Exchange(ref _pending, 1);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_configured)
        {
            _effect.Reset();
        }
    }

    public void ApplyPendingUpdates()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0)
        {
            return;
        }

        var gain = Volatile.Read(ref _pendingGainDb);
        _effect.GainDb = gain;
        Volatile.Write(ref _gainDb, gain);
        _effect.Bypass = Volatile.Read(ref _enabled) == 0;
    }

    public void Process(Span<float> buffer, in AudioFormat format)
    {
        if (!_configured || buffer.IsEmpty)
        {
            return;
        }

        _effect.Process(buffer);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
