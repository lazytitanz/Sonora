using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Negotiates an exclusive-mode session format without attaching a PCM playback consumer.
/// </summary>
internal static class ExclusiveFormatNegotiator
{
    /// <summary>
    /// Probes exclusive capability for <paramref name="preferred"/> on <paramref name="device"/>.
    /// Uses <see cref="MMDevice.AudioClient"/> format queries only — does not build a
    /// <see cref="WasapiPlayer"/> or call <c>Initialize</c>, so the endpoint is not taken
    /// exclusively during the probe (avoids exclusive open/close churn before the real open).
    /// </summary>
    public static AudioOutputFormatProbeResult Probe(MMDevice device, AudioFormat preferred)
    {
        ArgumentNullException.ThrowIfNull(device);

        AudioClient? client = null;
        try
        {
            client = device.CreateAudioClient();
            var preferredWave = WaveFormat.CreateIeeeFloatWaveFormat(preferred.SampleRate, preferred.ChannelCount);
            if (client.IsFormatSupported(AudioClientShareMode.Exclusive, preferredWave))
            {
                return new AudioOutputFormatProbeResult(
                    Supported: true,
                    RequiredSessionFormat: AudioFormat.CreateFloat32(preferred.SampleRate, preferred.ChannelCount));
            }

            var supported = FindSupportedExclusiveFormat(client, preferredWave);
            if (supported is null)
            {
                foreach (var bits in new[] { 32, 24, 16 })
                {
                    var pcmPreferred = new WaveFormat(preferred.SampleRate, bits, preferred.ChannelCount);
                    supported = FindSupportedExclusiveFormat(client, pcmPreferred);
                    if (supported is not null)
                    {
                        break;
                    }
                }
            }

            if (supported is null)
            {
                return new AudioOutputFormatProbeResult(
                    Supported: false,
                    RequiredSessionFormat: preferred,
                    FailureKind: AudioOutputFailureKind.UnsupportedFormat);
            }

            return new AudioOutputFormatProbeResult(
                Supported: true,
                RequiredSessionFormat: AudioFormat.CreateFloat32(supported.SampleRate, supported.Channels));
        }
        catch (Exception ex)
        {
            var kind = WasapiErrorMapper.Classify(ex);
            if (kind == AudioOutputFailureKind.Unspecified)
            {
                kind = AudioOutputFailureKind.InitializationFailed;
            }

            return new AudioOutputFormatProbeResult(
                Supported: false,
                RequiredSessionFormat: preferred,
                FailureKind: kind);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static WaveFormat? FindSupportedExclusiveFormat(AudioClient client, WaveFormat preferred)
    {
        // Mirror WasapiPlayer.GetSupportedExclusiveFormat: try exact, then channel/bit variants at same rate.
        if (client.IsFormatSupported(AudioClientShareMode.Exclusive, preferred))
        {
            return preferred;
        }

        foreach (var channels in Distinct(preferred.Channels, 2, 1, 6, 8))
        {
            foreach (var bits in Distinct(preferred.BitsPerSample, 32, 24, 16))
            {
                WaveFormat candidate = preferred.Encoding == WaveFormatEncoding.IeeeFloat && bits == 32
                    ? WaveFormat.CreateIeeeFloatWaveFormat(preferred.SampleRate, channels)
                    : new WaveFormat(preferred.SampleRate, bits, channels);

                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<int> Distinct(int first, params int[] rest)
    {
        yield return first;
        foreach (var value in rest)
        {
            if (value != first)
            {
                yield return value;
            }
        }
    }
}
