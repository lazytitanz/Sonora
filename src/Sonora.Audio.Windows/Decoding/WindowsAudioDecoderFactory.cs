using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Creates <see cref="WindowsAudioDecoder"/> instances for local files.
/// </summary>
public sealed class WindowsAudioDecoderFactory : IAudioDecoderFactory
{
    /// <inheritdoc />
    public Task<IAudioDecoder> CreateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // Opening readers may touch disk/codecs; keep off the audio callback by design (LoadAsync/pump only).
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IAudioDecoder decoder = WindowsAudioDecoder.Open(filePath);
            return decoder;
        }, cancellationToken);
    }
}
