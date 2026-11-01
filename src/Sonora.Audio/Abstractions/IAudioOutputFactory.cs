namespace Sonora.Audio.Abstractions;

/// <summary>
/// Creates <see cref="IAudioOutput"/> instances for the host platform.
/// </summary>
public interface IAudioOutputFactory
{
    /// <summary>
    /// Creates an output bound to the specified render endpoint, or the system default when
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
}
