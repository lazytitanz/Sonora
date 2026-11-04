using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioEffectFactory : IAudioEffectFactory
{
    public IEqualizerEffect CreateEqualizer(EqualizerOptions options)
        => new FakeEqualizerEffect(options);

    public IPreampEffect CreatePreamp(float gainDb)
        => new FakePreampEffect(gainDb);

    public ILimiterEffect CreateLimiter(LimiterOptions options)
        => new FakeLimiterEffect(options);
}

internal abstract class FakeEffectBase : IAudioEffectRuntime
{
    private int _enabled = 1;
    public int ConfigureCount;
    public int ResetCount;
    public int ProcessCount;
    public int ApplyPendingCount;
    public bool Disposed;

    public abstract string Name { get; }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) == 1;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public virtual void Configure(in AudioFormat format) => ConfigureCount++;

    public void Reset() => ResetCount++;

    public void ApplyPendingUpdates() => ApplyPendingCount++;

    public virtual void Process(Span<float> buffer, in AudioFormat format)
    {
        ProcessCount++;
        if (!Enabled)
        {
            return;
        }

        ProcessCore(buffer, format);
    }

    protected abstract void ProcessCore(Span<float> buffer, in AudioFormat format);

    public virtual int LatencySamples => 0;

    public virtual int MaxLatencySamples => 0;

    public void Dispose() => Disposed = true;
}

internal sealed class FakePreampEffect : FakeEffectBase, IPreampEffect
{
    public FakePreampEffect(float gainDb) => GainDb = gainDb;
    public override string Name => "FakePreamp";
    public float GainDb { get; set; }

    protected override void ProcessCore(Span<float> buffer, in AudioFormat format)
    {
        var linear = MathF.Pow(10f, GainDb / 20f);
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= linear;
        }
    }
}

internal sealed class FakeLimiterEffect : FakeEffectBase, ILimiterEffect
{
    private readonly float _lookaheadMs;
    private int _maxLatencySamples;

    public FakeLimiterEffect(LimiterOptions options)
    {
        CeilingDb = options.CeilingDb;
        ReleaseMs = options.ReleaseMs;
        _lookaheadMs = options.LookaheadMs;
    }

    public override string Name => "FakeLimiter";
    public float CeilingDb { get; set; }
    public float ReleaseMs { get; set; }

    public override int LatencySamples => Enabled ? _maxLatencySamples : 0;

    public override int MaxLatencySamples => _maxLatencySamples;

    public override void Configure(in AudioFormat format)
    {
        base.Configure(format);
        _maxLatencySamples = Math.Max(1, (int)Math.Round(_lookaheadMs * format.SampleRate / 1000.0));
    }

    protected override void ProcessCore(Span<float> buffer, in AudioFormat format)
    {
        var ceiling = MathF.Pow(10f, CeilingDb / 20f);
        for (var i = 0; i < buffer.Length; i++)
        {
            var s = buffer[i];
            if (s > ceiling)
            {
                buffer[i] = ceiling;
            }
            else if (s < -ceiling)
            {
                buffer[i] = -ceiling;
            }
        }
    }
}

internal sealed class FakeEqualizerEffect : FakeEffectBase, IEqualizerEffect
{
    private readonly EqualizerBandConfig[] _bands;
    private readonly float[] _gains;

    public FakeEqualizerEffect(EqualizerOptions options)
    {
        _bands = options.Bands.ToArray();
        _gains = _bands.Select(b => b.GainDb).ToArray();
    }

    public override string Name => "FakeEqualizer";
    public int BandCount => _bands.Length;
    public float GetBandFrequency(int bandIndex) => _bands[bandIndex].FrequencyHz;
    public float GetBandGain(int bandIndex) => _gains[bandIndex];
    public void SetBandGain(int bandIndex, float gainDb) => _gains[bandIndex] = gainDb;
    public bool GetBandEnabled(int bandIndex) => _bands[bandIndex].Enabled;
    public void SetBandEnabled(int bandIndex, bool enabled) { }
    public void ApplyPreset(EqualizerPreset preset)
    {
        for (var i = 0; i < BandCount; i++)
        {
            _gains[i] = preset.Bands[i].GainDb;
        }
    }

    protected override void ProcessCore(Span<float> buffer, in AudioFormat format)
    {
        // Identity for rack ordering tests; gains are inspected via API.
    }
}

/// <summary>Multiplies by a constant so rack order can be observed.</summary>
internal sealed class MultiplyEffect : FakeEffectBase
{
    private readonly float _factor;
    public MultiplyEffect(string name, float factor)
    {
        Name = name;
        _factor = factor;
    }

    public override string Name { get; }
    protected override void ProcessCore(Span<float> buffer, in AudioFormat format)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= _factor;
        }
    }
}
