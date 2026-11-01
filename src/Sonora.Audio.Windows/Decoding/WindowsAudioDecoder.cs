using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Opens local audio files through the Windows decoder selector.
/// Prefer this entry point from tests and <see cref="WindowsAudioDecoderFactory"/>.
/// </summary>
internal static class WindowsAudioDecoder
{
    public static IAudioDecoder Open(string filePath) => DecoderSelector.Open(filePath);
}
