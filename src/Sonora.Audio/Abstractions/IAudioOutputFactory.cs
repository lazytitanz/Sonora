namespace Sonora.Audio.Abstractions;

/// <summary>
/// Creates <see cref="IAudioOutput"/> instances for the host platform.
/// </summary>
public interface IAudioOutputFactory
{
    /// <summary>
    /// Creates an output bound to the default render device.
    /// </summary>
    IAudioOutput CreateDefaultOutput();
}
