using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

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
    /// <param name="position">
    /// Requested source timeline position. Negative values are clamped to zero.
    /// Positions beyond <see cref="Duration"/> are clamped when duration is known.
    /// The request is interpreted with floor frame rounding
    /// (<c>frameIndex = floor(seconds × sampleRate)</c>).
    /// </param>
    /// <returns>
    /// Seek outcome. Timestamps are source-decoder PCM frame times.
    /// <see cref="AudioSeekAccuracy.Exact"/> requires proven first-frame correspondence;
    /// accepting a container or Media Foundation timestamp alone is never sufficient for Exact.
    /// <see cref="AudioSeekAccuracy.Unknown"/> reports a null <see cref="AudioSeekResult.ActualPosition"/>.
    /// </returns>
    /// <remarks>
    /// Breaking change in 0.7.0: previously returned <see langword="void"/>.
    /// Not safe to call from real-time audio callbacks.
    /// </remarks>
    AudioSeekResult Seek(TimeSpan position);
}
