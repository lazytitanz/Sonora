using Sonora.Audio.Formats;
using Sonora.Audio.Output;

namespace Sonora.Audio.Abstractions;

/// <summary>
/// Creates <see cref="IAudioOutput"/> instances for the host platform.
/// </summary>
public interface IAudioOutputFactory
{
    /// <summary>
    /// Creates a shared-mode output bound to the specified render endpoint, or the system default when
    /// <paramref name="deviceId"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="deviceId">
    /// Stable endpoint id, or <see langword="null"/> to follow the platform default
    /// (Windows: Multimedia render role via notification-driven rebinds).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an output for the given request (shared or exclusive).
    /// </summary>
    Task<IAudioOutput> CreateOutputAsync(
        AudioOutputCreateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-destructively probes whether exclusive output can support a format near
    /// <paramref name="preferredSessionFormat"/> on the given endpoint.
    /// Must not consume PCM from a live Sonora session or attach a second playback consumer.
    /// </summary>
    /// <remarks>
    /// Shared-mode requests return <paramref name="preferredSessionFormat"/> as supported without device I/O.
    /// Backends that do not support exclusive mode return <see cref="AudioOutputFailureKind.ExclusiveModeRestricted"/>.
    /// </remarks>
    Task<AudioOutputFormatProbeResult> ProbeOutputFormatAsync(
        AudioOutputCreateRequest request,
        AudioFormat preferredSessionFormat,
        CancellationToken cancellationToken = default);
}
