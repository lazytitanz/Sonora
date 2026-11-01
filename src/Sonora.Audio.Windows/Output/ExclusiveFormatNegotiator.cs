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
    /// Builds a temporary exclusive <see cref="WasapiPlayer"/> for format queries only; never calls Init with PCM.
    /// </summary>
    public static AudioOutputFormatProbeResult Probe(MMDevice device, AudioFormat preferred)
    {
        ArgumentNullException.ThrowIfNull(device);

        WasapiPlayer? player = null;
        try
        {
            player = new WasapiPlayerBuilder()
                .WithExclusiveMode()
                .WithEventSync()
                .WithDevice(device)
                .WithLatency(50)
                .Build();

            var preferredWave = WaveFormat.CreateIeeeFloatWaveFormat(preferred.SampleRate, preferred.ChannelCount);
            if (player.IsFormatSupported(preferredWave))
            {
                return new AudioOutputFormatProbeResult(
                    Supported: true,
                    RequiredSessionFormat: AudioFormat.CreateFloat32(preferred.SampleRate, preferred.ChannelCount));
            }

            var supported = player.GetSupportedExclusiveFormat(preferredWave);
            if (supported is null)
            {
                // Try PCM 16/24/32 at the preferred rate via GetSupportedExclusiveFormat from int preferred.
                foreach (var bits in new[] { 32, 24, 16 })
                {
                    var pcmPreferred = new WaveFormat(preferred.SampleRate, bits, preferred.ChannelCount);
                    supported = player.GetSupportedExclusiveFormat(pcmPreferred);
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
            player?.Dispose();
        }
    }
}
