using Sonora.Audio.Formats;

namespace Sonora.Audio.Abstractions;

/// <summary>
/// Platform audio output backend. Consumes already-buffered PCM via <see cref="IPcmSampleSource"/>.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Output format negotiated with the device, when available.</summary>
    AudioFormat? Format { get; }

    /// <summary>
    /// Stable host endpoint id this output was opened on, when known; otherwise <see langword="null"/>.
    /// </summary>
    string? EndpointId { get; }

    /// <summary>
    /// Steady-state output latency estimate. <see cref="TimeSpan.Zero"/> when unknown.
    /// </summary>
    TimeSpan AverageLatency { get; }

    /// <summary>
    /// Current output latency estimate (padding). Falls back to <see cref="AverageLatency"/> when unavailable.
    /// </summary>
    TimeSpan CurrentLatency { get; }

    /// <summary>
    /// Bytes or samples already rendered by the device clock, expressed as a timeline offset from stream start,
    /// when the backend can report it; otherwise <see langword="null"/>.
    /// </summary>
    TimeSpan? TryGetRenderedPosition();

    /// <summary>
    /// Initializes the device to pull from <paramref name="source"/>. May only be called once per instance.
    /// </summary>
    void Initialize(IPcmSampleSource source);

    /// <summary>Starts or resumes playback.</summary>
    void Play();

    /// <summary>Pauses playback, preserving device position when possible.</summary>
    void Pause();

    /// <summary>Stops playback.</summary>
    void Stop();

    /// <summary>
    /// Raised when the output stops because the source reached end-of-stream (natural completion)
    /// or because of an error. The boolean is <see langword="true"/> for natural completion.
    /// </summary>
    event EventHandler<AudioOutputStoppedEventArgs>? Stopped;
}

/// <summary>
/// Arguments for <see cref="IAudioOutput.Stopped"/>.
/// </summary>
public sealed class AudioOutputStoppedEventArgs : EventArgs
{
    /// <summary>
    /// Creates event args.
    /// </summary>
    public AudioOutputStoppedEventArgs(bool reachedEndOfStream, Exception? exception = null)
    {
        ReachedEndOfStream = reachedEndOfStream;
        Exception = exception;
    }

    /// <summary>Whether the source drained normally.</summary>
    public bool ReachedEndOfStream { get; }

    /// <summary>Failure that caused the stop, if any.</summary>
    public Exception? Exception { get; }
}
