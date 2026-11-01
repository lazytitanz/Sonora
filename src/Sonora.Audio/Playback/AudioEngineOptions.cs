using Sonora.Audio.DSP;

namespace Sonora.Audio.Playback;

/// <summary>
/// Configuration for <see cref="AudioEngine"/> and players it creates.
/// </summary>
public sealed class AudioEngineOptions
{
    /// <summary>
    /// Target ring-buffer duration used to size the PCM buffer. Default is 1 second.
    /// </summary>
    public TimeSpan BufferDuration { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Minimum buffered duration before <see cref="AudioPlayer.Play"/> starts device output after a load/seek.
    /// Default is 50 ms.
    /// </summary>
    public TimeSpan PrimeDuration { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Optional PCM processors applied on the decode-pump path before samples enter the ring buffer.
    /// Prefer <see cref="AudioPlayer.Effects"/> for interactive EQ/preamp/limiter (post-ring, live updates).
    /// </summary>
    public IReadOnlyList<IAudioProcessor> Processors { get; init; } = Array.Empty<IAudioProcessor>();

    /// <summary>
    /// Factory used to create platform-backed DSP effects for <see cref="AudioPlayer.Effects"/>.
    /// Set automatically by <c>SonoraWindows.CreateEngine()</c>.
    /// </summary>
    public IAudioEffectFactory? EffectFactory { get; init; }

    /// <summary>
    /// Converts preloaded/loaded decoders to the session output format for gapless joins.
    /// Defaults to <see cref="PassthroughFormatConverter"/> (channel mix only).
    /// <c>SonoraWindows.CreateEngine()</c> supplies a WDL-backed converter.
    /// </summary>
    public IAudioFormatConverter? FormatConverter { get; init; }
}
