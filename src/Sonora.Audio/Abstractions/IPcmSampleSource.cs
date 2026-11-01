using Sonora.Audio.Formats;

namespace Sonora.Audio.Abstractions;

/// <summary>
/// Non-blocking source of interleaved float32 PCM for audio output backends.
/// Implementations must not perform disk I/O or decoding.
/// </summary>
public interface IPcmSampleSource
{
    /// <summary>PCM format of samples returned by <see cref="Read"/>.</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Attempts to read interleaved samples. Short reads should be filled with silence by the caller when playing.
    /// </summary>
    /// <returns>Number of interleaved samples written.</returns>
    int Read(Span<float> destination);

    /// <summary>
    /// <see langword="true"/> when the decoder has reached EOF and the buffer has been fully drained.
    /// </summary>
    bool IsEndOfStream { get; }
}
