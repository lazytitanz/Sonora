using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Opens local audio files and streams through the Windows decoder selector.
/// Prefer this entry point from tests and <see cref="WindowsAudioDecoderFactory"/>.
/// </summary>
internal static class WindowsAudioDecoder
{
    public static IAudioDecoder Open(string filePath) => DecoderSelector.Open(filePath);

    /// <summary>
    /// Opens a decoder for an audio byte stream. The returned decoder owns <paramref name="stream"/>.
    /// </summary>
    public static IAudioDecoder Open(
        Stream stream,
        string? originName = null,
        string? contentTypeHint = null)
        => DecoderSelector.Open(stream, originName, contentTypeHint);
}
