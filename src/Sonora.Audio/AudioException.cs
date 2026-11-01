using Sonora.Audio.Output;

namespace Sonora.Audio;

/// <summary>
/// Base exception for Sonora audio failures.
/// </summary>
public class AudioException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public AudioException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and inner exception.</summary>
    public AudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an audio format or codec is not supported.
/// </summary>
public sealed class UnsupportedFormatException : AudioException
{
    /// <inheritdoc cref="AudioException(string)"/>
    public UnsupportedFormatException(string message)
        : base(message)
    {
    }

    /// <inheritdoc cref="AudioException(string, Exception)"/>
    public UnsupportedFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an audio file is invalid, truncated, or unreadable as audio.
/// </summary>
public sealed class InvalidAudioException : AudioException
{
    /// <inheritdoc cref="AudioException(string)"/>
    public InvalidAudioException(string message)
        : base(message)
    {
    }

    /// <inheritdoc cref="AudioException(string, Exception)"/>
    public InvalidAudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when decoding fails after a source was opened.
/// </summary>
public sealed class DecoderException : AudioException
{
    /// <inheritdoc cref="AudioException(string)"/>
    public DecoderException(string message)
        : base(message)
    {
    }

    /// <inheritdoc cref="AudioException(string, Exception)"/>
    public DecoderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the audio output device fails.
/// </summary>
public sealed class AudioOutputException : AudioException
{
    /// <inheritdoc cref="AudioException(string)"/>
    public AudioOutputException(string message)
        : this(message, AudioOutputFailureKind.Unspecified)
    {
    }

    /// <inheritdoc cref="AudioException(string, Exception)"/>
    public AudioOutputException(string message, Exception innerException)
        : this(message, AudioOutputFailureKind.Unspecified, innerException)
    {
    }

    /// <summary>Creates an exception with a classified failure kind.</summary>
    public AudioOutputException(string message, AudioOutputFailureKind failureKind)
        : base(message)
    {
        FailureKind = failureKind;
    }

    /// <summary>Creates an exception with a classified failure kind and inner exception.</summary>
    public AudioOutputException(string message, AudioOutputFailureKind failureKind, Exception innerException)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    /// <summary>Classified cause for fallback and UI decisions.</summary>
    public AudioOutputFailureKind FailureKind { get; }
}

/// <summary>
/// Thrown when an output device selection is invalid or the endpoint cannot be resolved.
/// </summary>
public sealed class AudioDeviceException : AudioException
{
    /// <inheritdoc cref="AudioException(string)"/>
    public AudioDeviceException(string message)
        : base(message)
    {
    }

    /// <inheritdoc cref="AudioException(string, Exception)"/>
    public AudioDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when HTTP/HTTPS audio transport fails (connect, range validation, reconnect exhaustion, etc.).
/// Messages must not include credentials, authorization headers, or signed URL query parameters.
/// </summary>
public sealed class NetworkAudioException : AudioException
{
    /// <inheritdoc cref="AudioException(string)"/>
    public NetworkAudioException(string message)
        : base(message)
    {
    }

    /// <inheritdoc cref="AudioException(string, Exception)"/>
    public NetworkAudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
