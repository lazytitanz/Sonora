using NAudio.Effects;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Windows.DSP;

/// <summary>Clipping protection wrapping NAudio <see cref="LimiterEffect"/>.</summary>
internal sealed class NAudioLimiterEffect : ILimiterEffect, IAudioEffectRuntime
{
    private readonly LimiterEffect _effect = new();
    private float _ceilingDb;
    private float _pendingCeilingDb;
    private float _releaseMs;
    private float _pendingReleaseMs;
    private int _enabled = 1;
    private int _pending;
    private bool _configured;
    private bool _disposed;

    public NAudioLimiterEffect(LimiterOptions options)
    {
        _ceilingDb = options.CeilingDb;
        _pendingCeilingDb = options.CeilingDb;
        _releaseMs = options.ReleaseMs;
        _pendingReleaseMs = options.ReleaseMs;
        _effect.CeilingDb = options.CeilingDb;
        _effect.ReleaseMs = options.ReleaseMs;
        _effect.LookaheadMs = options.LookaheadMs;
        _effect.TruePeak = false;
        Interlocked.Exchange(ref _pending, 1);
    }

    public string Name => "Limiter";

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) == 1;
        set
        {
            Volatile.Write(ref _enabled, value ? 1 : 0);
            Interlocked.Exchange(ref _pending, 1);
        }
    }

    public float CeilingDb
    {
        get => Volatile.Read(ref _ceilingDb);
        set
        {
            Volatile.Write(ref _pendingCeilingDb, value);
            Interlocked.Exchange(ref _pending, 1);
        }
    }

    public float ReleaseMs
    {
        get => Volatile.Read(ref _releaseMs);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0f);
            Volatile.Write(ref _pendingReleaseMs, value);
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

        var ceiling = Volatile.Read(ref _pendingCeilingDb);
        var release = Volatile.Read(ref _pendingReleaseMs);
        _effect.CeilingDb = ceiling;
        _effect.ReleaseMs = release;
        Volatile.Write(ref _ceilingDb, ceiling);
        Volatile.Write(ref _releaseMs, release);
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
