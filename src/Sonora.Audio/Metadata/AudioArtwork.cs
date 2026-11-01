namespace Sonora.Audio.Metadata;

/// <summary>
/// Embedded album artwork. <see cref="Data"/> is an owned copy of the encoded image bytes
/// and remains valid after the metadata reader and source file are disposed.
/// Sonora does not decode, resize, or recompress images.
/// </summary>
/// <param name="MimeType">MIME type when known (for example <c>image/jpeg</c>); otherwise <see langword="null"/>.</param>
/// <param name="Description">Optional picture description from the tag.</param>
/// <param name="Kind">Normalized picture kind.</param>
/// <param name="Data">Owned encoded image bytes. Empty when the picture could not be copied.</param>
public sealed record AudioArtwork(
    string? MimeType,
    string? Description,
    AudioArtworkKind Kind,
    ReadOnlyMemory<byte> Data);
