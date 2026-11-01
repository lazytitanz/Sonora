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
        : base(message)
    {
    }

    /// <inheritdoc cref="AudioException(string, Exception)"/>
    public AudioOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
