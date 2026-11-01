namespace Sonora.Audio.Windows.Decoding;

/// <summary>
/// Result of sniffing an audio container / bitstream for decoder routing.
/// </summary>
internal enum AudioContainerKind
{
    Unknown = 0,
    Wave,
    Aiff,
    Flac,
    MpegAudio,
    Mpeg4,
    AdtsAac,
    Asf,
    OggVorbis,
    OggOpus,
    WebM,
}
