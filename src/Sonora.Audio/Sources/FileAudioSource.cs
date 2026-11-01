namespace Sonora.Audio.Sources;

/// <summary>Local filesystem audio source.</summary>
/// <param name="Path">Absolute or relative path to an audio file.</param>
public sealed record FileAudioSource(string Path) : AudioSource
{
    /// <inheritdoc />
    public override string Identifier => Path;

    /// <summary>Creates a file source after validating the path is non-empty.</summary>
    public static FileAudioSource Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileAudioSource(path);
    }
}
