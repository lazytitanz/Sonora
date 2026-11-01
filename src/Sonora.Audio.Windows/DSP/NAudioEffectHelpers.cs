using NAudio.Wave;
using Sonora.Audio.Formats;

namespace Sonora.Audio.Windows.DSP;

internal static class NAudioEffectHelpers
{
    public static WaveFormat ToIeeeFloatWaveFormat(in AudioFormat format)
    {
        if (format.SampleFormat != SampleFormat.Float32)
        {
            throw new NotSupportedException(
                $"Sonora DSP requires interleaved float32 PCM; received {format.SampleFormat}.");
        }

        return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.ChannelCount);
    }
}
