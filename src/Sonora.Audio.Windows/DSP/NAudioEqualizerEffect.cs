using NAudio.Effects;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Windows.DSP;

/// <summary>Multiband equalizer wrapping NAudio <see cref="Equalizer"/>.</summary>
internal sealed class NAudioEqualizerEffect : IEqualizerEffect, IAudioEffectRuntime
{
    private readonly EqualizerBand[] _bands;
    private readonly Equalizer _equalizer;
    private readonly float[] _pendingGainsDb;
    private readonly int[] _pendingEnabled; // 1/0 per band
    private readonly float[] _publishedGainsDb;
    private readonly int[] _publishedBandEnabled;
    private int _enabled = 1;
    private int _pending;
    private bool _configured;
    private bool _disposed;

    public NAudioEqualizerEffect(EqualizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _bands = new EqualizerBand[options.Bands.Count];
        _pendingGainsDb = new float[options.Bands.Count];
        _pendingEnabled = new int[options.Bands.Count];
        _publishedGainsDb = new float[options.Bands.Count];
        _publishedBandEnabled = new int[options.Bands.Count];

        for (var i = 0; i < options.Bands.Count; i++)
        {
            var cfg = options.Bands[i];
            _bands[i] = CreateBand(cfg);
            _pendingGainsDb[i] = cfg.GainDb;
            _publishedGainsDb[i] = cfg.GainDb;
            _pendingEnabled[i] = cfg.Enabled ? 1 : 0;
            _publishedBandEnabled[i] = cfg.Enabled ? 1 : 0;
            ApplyBandGainToNAudio(i, cfg.Enabled ? cfg.GainDb : 0f);
        }

        _equalizer = new Equalizer(_bands);
        Interlocked.Exchange(ref _pending, 1);
    }

    public string Name => "Equalizer";

    public int BandCount => _bands.Length;

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) == 1;
        set
        {
            Volatile.Write(ref _enabled, value ? 1 : 0);
            Interlocked.Exchange(ref _pending, 1);
        }
    }

    public float GetBandFrequency(int bandIndex)
    {
        ValidateBand(bandIndex);
        return _bands[bandIndex].Frequency;
    }

    public float GetBandGain(int bandIndex)
    {
        ValidateBand(bandIndex);
        // Return the pending/user-facing value so UI reads match SetBandGain immediately.
        return Volatile.Read(ref _pendingGainsDb[bandIndex]);
    }

    public void SetBandGain(int bandIndex, float gainDb)
    {
        ValidateBand(bandIndex);
        Volatile.Write(ref _pendingGainsDb[bandIndex], gainDb);
        Interlocked.Exchange(ref _pending, 1);
    }

    public bool GetBandEnabled(int bandIndex)
    {
        ValidateBand(bandIndex);
        return Volatile.Read(ref _pendingEnabled[bandIndex]) == 1;
    }

    public void SetBandEnabled(int bandIndex, bool enabled)
    {
        ValidateBand(bandIndex);
        Volatile.Write(ref _pendingEnabled[bandIndex], enabled ? 1 : 0);
        Interlocked.Exchange(ref _pending, 1);
    }

    public void ApplyPreset(EqualizerPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Bands.Count != BandCount)
        {
            throw new ArgumentException(
                $"Preset '{preset.Name}' has {preset.Bands.Count} bands but this equalizer has {BandCount}.",
                nameof(preset));
        }

        for (var i = 0; i < BandCount; i++)
        {
            var band = preset.Bands[i];
            Volatile.Write(ref _pendingGainsDb[i], band.GainDb);
            Volatile.Write(ref _pendingEnabled[i], band.Enabled ? 1 : 0);
        }

        Interlocked.Exchange(ref _pending, 1);
    }

    public void Configure(in AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _equalizer.Configure(NAudioEffectHelpers.ToIeeeFloatWaveFormat(format));
        _configured = true;
        Interlocked.Exchange(ref _pending, 1);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_configured)
        {
            _equalizer.Reset();
        }
    }

    public void ApplyPendingUpdates()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0)
        {
            return;
        }

        for (var i = 0; i < _bands.Length; i++)
        {
            var gain = Volatile.Read(ref _pendingGainsDb[i]);
            var bandEnabled = Volatile.Read(ref _pendingEnabled[i]) == 1;
            Volatile.Write(ref _publishedGainsDb[i], gain);
            Volatile.Write(ref _publishedBandEnabled[i], bandEnabled ? 1 : 0);
            ApplyBandGainToNAudio(i, bandEnabled ? gain : 0f);
        }

        // Click-free coefficient update — must run on the audio thread only.
        _equalizer.Update();
        _equalizer.Bypass = Volatile.Read(ref _enabled) == 0;
    }

    public void Process(Span<float> buffer, in AudioFormat format)
    {
        if (!_configured || buffer.IsEmpty)
        {
            return;
        }

        _equalizer.Process(buffer);
    }

    public int LatencySamples => 0;

    public int MaxLatencySamples => 0;

    public void Dispose()
    {
        _disposed = true;
    }

    private void ValidateBand(int bandIndex)
    {
        if ((uint)bandIndex >= (uint)_bands.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bandIndex));
        }
    }

    private void ApplyBandGainToNAudio(int index, float gainDb)
    {
        _bands[index].GainDb = gainDb;
    }

    private static EqualizerBand CreateBand(EqualizerBandConfig cfg)
    {
        return cfg.Type switch
        {
            Sonora.Audio.DSP.EqualizerBandType.LowShelf => EqualizerBand.LowShelf(cfg.FrequencyHz, cfg.GainDb, cfg.Q),
            Sonora.Audio.DSP.EqualizerBandType.HighShelf => EqualizerBand.HighShelf(cfg.FrequencyHz, cfg.GainDb, cfg.Q),
            _ => EqualizerBand.Peaking(cfg.FrequencyHz, cfg.GainDb, cfg.Q),
        };
    }
}
