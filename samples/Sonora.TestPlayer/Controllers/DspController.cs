using Sonora.Audio;
using Sonora.Audio.DSP;
using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Controllers;

/// <summary>
/// Configures and controls the player's DSP rack in the recommended order:
/// Preamp → six-band Equalizer → sample-peak Limiter.
/// </summary>
/// <remarks>
/// <para><b>Ownership.</b> The effects belong to the player's rack and are disposed with the player; this controller
/// holds references only and has nothing to dispose.</para>
/// <para><b>Look-ahead is construction-time.</b> The limiter's look-ahead (<see cref="LimiterOptions.LookaheadMs"/>)
/// is fixed when the limiter is created in the constructor (<see cref="LimiterOptions"/> defaults: 0 dBFS ceiling,
/// 100 ms release, 5 ms look-ahead). Only ceiling and release can change at runtime; the reported look-ahead is the
/// construction value. Changing it would require removing and re-adding the limiter.</para>
/// <para><b>Threading.</b> Safe to call from any thread; effect parameter changes are applied by the engine on the
/// audio thread at block boundaries.</para>
/// </remarks>
public sealed class DspController
{
    /// <summary>Allowed preamp and equalizer band gain range, in dB.</summary>
    public const float MaxGainDb = 24f;

    /// <summary>Lowest permitted limiter ceiling, in dBFS.</summary>
    public const float MinCeilingDb = -60f;

    /// <summary>Longest permitted limiter release, in ms.</summary>
    public const float MaxReleaseMs = 5000f;

    private readonly AudioPlayer _player;
    private readonly DiagnosticEventStore _store;
    private readonly IPreampEffect _preamp;
    private readonly IEqualizerEffect _equalizer;
    private readonly ILimiterEffect _limiter;
    private readonly LimiterOptions _limiterOptions;
    private readonly object _sync = new();
    private string? _activePreset;

    /// <summary>
    /// Adds Preamp, Equalizer (<see cref="EqualizerOptions.CreateSixBand"/>) and Limiter (<c>new LimiterOptions()</c>),
    /// in that order, to the player's rack.
    /// </summary>
    public DspController(AudioPlayer player, DiagnosticEventStore store)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        var rack = _player.Effects;
        _limiterOptions = new LimiterOptions();
        _preamp = rack.AddPreamp();
        _equalizer = rack.AddEqualizer(EqualizerOptions.CreateSixBand());
        _limiter = rack.AddLimiter(_limiterOptions);
        _activePreset = EqualizerPresets.Flat.Name;

        _store.Add("dsp", DiagnosticLevel.Info, "dsp.initialized", "DSP rack initialized: Preamp → Equalizer(6-band) → Limiter.");
    }

    /// <summary>Returns the current DSP configuration.</summary>
    public DspStateDto GetState()
    {
        var bands = new EqBandDto[_equalizer.BandCount];
        for (var i = 0; i < bands.Length; i++)
        {
            bands[i] = new EqBandDto(i, _equalizer.GetBandFrequency(i), _equalizer.GetBandGain(i), _equalizer.GetBandEnabled(i));
        }

        string? preset;
        lock (_sync)
        {
            preset = _activePreset;
        }

        return new DspStateDto
        {
            RackBypass = _player.Effects.Bypass,
            PreampEnabled = _preamp.Enabled,
            PreampGainDb = _preamp.GainDb,
            EqualizerEnabled = _equalizer.Enabled,
            Bands = bands,
            ActivePreset = preset,
            PresetNames = EqualizerPresets.All.Select(p => p.Name).ToArray(),
            LimiterEnabled = _limiter.Enabled,
            LimiterCeilingDb = _limiter.CeilingDb,
            LimiterReleaseMs = _limiter.ReleaseMs,
            LimiterLookaheadMs = _limiterOptions.LookaheadMs,
            LimiterLookaheadIsConstructionTime = true,
        };
    }

    /// <summary>Bypasses (or re-enables) the entire rack.</summary>
    public DspStateDto SetRackBypass(bool bypass)
    {
        _player.Effects.Bypass = bypass;
        Log($"Rack bypass {(bypass ? "on" : "off")}.");
        return GetState();
    }

    /// <summary>Sets preamp gain in dB (−24…+24).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range or not finite.</exception>
    public DspStateDto SetPreampGainDb(float gainDb)
    {
        ValidateGain(gainDb, nameof(gainDb));
        _preamp.GainDb = gainDb;
        Log($"Preamp gain {gainDb:F1} dB.");
        return GetState();
    }

    /// <summary>Enables or disables the preamp.</summary>
    public DspStateDto SetPreampEnabled(bool enabled)
    {
        _preamp.Enabled = enabled;
        Log($"Preamp {(enabled ? "enabled" : "disabled")}.");
        return GetState();
    }

    /// <summary>Enables or disables the equalizer.</summary>
    public DspStateDto SetEqualizerEnabled(bool enabled)
    {
        _equalizer.Enabled = enabled;
        Log($"Equalizer {(enabled ? "enabled" : "disabled")}.");
        return GetState();
    }

    /// <summary>Sets one band's gain in dB (−24…+24). Clears the active preset name.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The band index or gain is invalid.</exception>
    public DspStateDto SetBandGain(int bandIndex, float gainDb)
    {
        ValidateBand(bandIndex);
        ValidateGain(gainDb, nameof(gainDb));
        _equalizer.SetBandGain(bandIndex, gainDb);
        SetActivePreset(null);
        Log($"EQ band {bandIndex} gain {gainDb:F1} dB.");
        return GetState();
    }

    /// <summary>Enables or disables one band. Clears the active preset name.</summary>
    public DspStateDto SetBandEnabled(int bandIndex, bool enabled)
    {
        ValidateBand(bandIndex);
        _equalizer.SetBandEnabled(bandIndex, enabled);
        SetActivePreset(null);
        Log($"EQ band {bandIndex} {(enabled ? "enabled" : "disabled")}.");
        return GetState();
    }

    /// <summary>Applies a built-in preset by name (case-insensitive). A preset's preamp gain is applied when it has one.</summary>
    /// <exception cref="ArgumentException">No preset has that name.</exception>
    public DspStateDto ApplyPreset(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var preset = EqualizerPresets.All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown equalizer preset '{name}'.", nameof(name));

        _equalizer.ApplyPreset(preset);
        if (preset.PreampGainDb is { } preamp)
        {
            _preamp.GainDb = preamp;
        }

        SetActivePreset(preset.Name);
        Log($"Applied EQ preset '{preset.Name}'.");
        return GetState();
    }

    /// <summary>Returns the equalizer to the Flat preset and the preamp to 0 dB.</summary>
    public DspStateDto Reset()
    {
        _equalizer.ApplyPreset(EqualizerPresets.Flat);
        _preamp.GainDb = 0f;
        SetActivePreset(EqualizerPresets.Flat.Name);
        Log("DSP reset to flat / 0 dB preamp.");
        return GetState();
    }

    /// <summary>Enables or disables the limiter.</summary>
    public DspStateDto SetLimiterEnabled(bool enabled)
    {
        _limiter.Enabled = enabled;
        Log($"Limiter {(enabled ? "enabled" : "disabled")}.");
        return GetState();
    }

    /// <summary>Sets the limiter ceiling in dBFS (−60…0).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range or not finite.</exception>
    public DspStateDto SetLimiterCeilingDb(float ceilingDb)
    {
        if (!float.IsFinite(ceilingDb) || ceilingDb is < MinCeilingDb or > 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(ceilingDb), ceilingDb, $"Ceiling must be between {MinCeilingDb} and 0 dBFS.");
        }

        _limiter.CeilingDb = ceilingDb;
        Log($"Limiter ceiling {ceilingDb:F1} dBFS.");
        return GetState();
    }

    /// <summary>Sets the limiter release in milliseconds (1…5000).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is out of range or not finite.</exception>
    public DspStateDto SetLimiterReleaseMs(float releaseMs)
    {
        if (!float.IsFinite(releaseMs) || releaseMs is < 1f or > MaxReleaseMs)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseMs), releaseMs, $"Release must be between 1 and {MaxReleaseMs} ms.");
        }

        _limiter.ReleaseMs = releaseMs;
        Log($"Limiter release {releaseMs:F0} ms.");
        return GetState();
    }

    private void ValidateBand(int bandIndex)
    {
        if (bandIndex < 0 || bandIndex >= _equalizer.BandCount)
        {
            throw new ArgumentOutOfRangeException(nameof(bandIndex), bandIndex, $"Band index must be 0–{_equalizer.BandCount - 1}.");
        }
    }

    private static void ValidateGain(float gainDb, string parameterName)
    {
        if (!float.IsFinite(gainDb) || gainDb is < -MaxGainDb or > MaxGainDb)
        {
            throw new ArgumentOutOfRangeException(parameterName, gainDb, $"Gain must be between -{MaxGainDb} and +{MaxGainDb} dB.");
        }
    }

    private void SetActivePreset(string? name)
    {
        lock (_sync)
        {
            _activePreset = name;
        }
    }

    private void Log(string message) => _store.Add("dsp", DiagnosticLevel.Debug, "dsp.changed", message);
}
