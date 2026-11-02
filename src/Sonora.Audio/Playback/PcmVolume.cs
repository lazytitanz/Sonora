namespace Sonora.Audio.Playback;

/// <summary>
/// Applies linear master volume to interleaved float PCM.
/// Used on the output pull path (after the ring, before the effect rack) so changes are audible
/// without waiting for buffered samples to drain.
/// </summary>
internal static class PcmVolume
{
    /// <summary>Multiplies <paramref name="buffer"/> by <paramref name="volume"/> in place.</summary>
    public static void Apply(Span<float> buffer, float volume)
    {
        if (buffer.IsEmpty || Math.Abs(volume - 1f) < 0.0001f)
        {
            return;
        }

        if (volume <= 0f)
        {
            buffer.Clear();
            return;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= volume;
        }
    }
}
