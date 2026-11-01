using Sonora.Audio.Formats;

namespace Sonora.Audio.Abstractions;

/// <summary>
/// Decodes an audio source into interleaved float32 PCM.
/// Implementations may perform disk or codec I/O and must never be called from real-time output callbacks.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Decoded PCM format exposed to the playback pipeline.</summary>
    AudioFormat Format { get; }

    /// <summary>Total duration when known; <see cref="TimeSpan.Zero"/> if unknown.</summary>
    TimeSpan Duration { get; }

    /// <summary>Whether seeking is supported for this source.</summary>
    bool CanSeek { get; }

    /// <summary>
    /// Reads interleaved float32 samples into <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of interleaved samples written; 0 indicates end of stream.</returns>
    int Read(Span<float> destination);

    /// <summary>
    /// Seeks to the specified timeline position when <see cref="CanSeek"/> is <see langword="true"/>.
    /// </summary>
    void Seek(TimeSpan position);
}
