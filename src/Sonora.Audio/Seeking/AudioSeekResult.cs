namespace Sonora.Audio.Seeking;

/// <summary>
/// Outcome of a seek against a decoder or player.
/// Timestamps refer to source-decoder PCM frames (the decoder's <c>Format.SampleRate</c>),
/// not output-session frames after resampling.
/// </summary>
/// <param name="RequestedPosition">
/// Clamped, frame-rounded position requested by the caller.
/// </param>
/// <param name="ActualPosition">
/// Position of the first retained source PCM frame after seeking, when established;
/// <see langword="null"/> when <see cref="Accuracy"/> is <see cref="AudioSeekAccuracy.Unknown"/>.
/// </param>
/// <param name="Accuracy">How precisely the landing was established.</param>
public sealed record AudioSeekResult(
    TimeSpan RequestedPosition,
    TimeSpan? ActualPosition,
    AudioSeekAccuracy Accuracy)
{
    /// <summary>Creates an exact seek result. <paramref name="actualPosition"/> must equal the frame-rounded request.</summary>
    public static AudioSeekResult Exact(TimeSpan requestedPosition, TimeSpan actualPosition)
    {
        if (actualPosition < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(actualPosition));
        }

        return new AudioSeekResult(requestedPosition, actualPosition, AudioSeekAccuracy.Exact);
    }

    /// <summary>
    /// Creates an approximate seek result with an evidence-backed landing position.
    /// Do not use container timestamps alone as <paramref name="actualPosition"/>.
    /// </summary>
    public static AudioSeekResult Approximate(TimeSpan requestedPosition, TimeSpan actualPosition)
    {
        if (actualPosition < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(actualPosition));
        }

        return new AudioSeekResult(requestedPosition, actualPosition, AudioSeekAccuracy.Approximate);
    }

    /// <summary>Creates an unknown seek result with no fabricated actual position.</summary>
    public static AudioSeekResult Unknown(TimeSpan requestedPosition)
        => new(requestedPosition, ActualPosition: null, AudioSeekAccuracy.Unknown);

    /// <summary>
    /// Validates invariants: Unknown ⇒ null actual; Exact/Approximate ⇒ non-null actual.
    /// </summary>
    public AudioSeekResult EnsureInvariants()
    {
        return Accuracy switch
        {
            AudioSeekAccuracy.Unknown when ActualPosition is not null
                => throw new InvalidOperationException("Unknown seek results must not report an actual position."),
            AudioSeekAccuracy.Exact when ActualPosition is null
                => throw new InvalidOperationException("Exact seek results must report an actual position."),
            AudioSeekAccuracy.Approximate when ActualPosition is null
                => throw new InvalidOperationException("Approximate seek results must report an evidence-backed actual position."),
            _ => this,
        };
    }
}
