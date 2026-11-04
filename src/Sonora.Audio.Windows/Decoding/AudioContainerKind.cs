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
    /// <summary>Fragmented MPEG-4 / DASH-style MP4 with <c>moof</c>/<c>mvex</c> (AAC via ManagedCodecs).</summary>
    FragmentedMpeg4,
    AdtsAac,
    Asf,
    OggVorbis,
    OggOpus,
    WebM,
}
