using Sonora.Audio.Formats;

namespace Sonora.Audio.Output;

/// <summary>
/// Result of a non-destructive exclusive-format capability probe.
/// Does not start playback or attach to a PCM consumer.
/// </summary>
/// <param name="Supported">Whether exclusive output appears feasible for the preferred format family.</param>
/// <param name="RequiredSessionFormat">
/// Float32 session format (rate/channels) Sonora should lock to before exclusive Init.
/// When <paramref name="Supported"/> is false, this may still carry the preferred format for diagnostics.
/// </param>
/// <param name="FailureKind">Classified failure when <paramref name="Supported"/> is false.</param>
public readonly record struct AudioOutputFormatProbeResult(
    bool Supported,
    AudioFormat RequiredSessionFormat,
    AudioOutputFailureKind FailureKind = AudioOutputFailureKind.Unspecified);
