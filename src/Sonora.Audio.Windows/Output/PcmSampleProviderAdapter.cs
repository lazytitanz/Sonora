using NAudio.Wave;
using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Adapts <see cref="IPcmSampleSource"/> to NAudio's <see cref="ISampleProvider"/>.
/// Reads only buffered PCM — no decoding or disk I/O.
/// </summary>
/// <remarks>
/// WASAPI exclusive event-driven mode requires each render period to be fully written.
/// A short read at end-of-stream (or returning 0 mid-period) causes
/// <c>AUDCLNT_E_BUFFER_SIZE_ERROR</c> in <c>AudioRenderClient.ReleaseBuffer</c>. This adapter
/// pads the final period with silence and returns 0 only on subsequent callbacks so exclusive
/// playback can finish cleanly.
/// </remarks>
internal sealed class PcmSampleProviderAdapter : ISampleProvider
{
    private IPcmSampleSource? _source;
    private readonly WaveFormat _waveFormat;
    private readonly Action? _onEndOfStream;
    private int _endOfStreamSignaled;
    private int _eosCallbackRaised;

    public PcmSampleProviderAdapter(IPcmSampleSource source, Action? onEndOfStream = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _onEndOfStream = onEndOfStream;
        var format = source.Format;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.ChannelCount);
    }

    public WaveFormat WaveFormat => _waveFormat;

    /// <summary>
    /// Prevents any further reads of the PCM source (used during output shutdown).
    /// </summary>
    public void Disconnect() => Volatile.Write(ref _source, null);

    public int Read(Span<float> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // After EOS has been delivered as a full (possibly silent) period, keep returning silence
        // rather than 0. Exclusive event-driven WASAPI throws AUDCLNT_E_BUFFER_SIZE_ERROR (0x88890016)
        // when ReleaseBuffer is called with a short/zero frame count. The output stops via
        // <see cref="_onEndOfStream"/> (or classifies the buffer-size HRESULT as clean EOS).
        if (Volatile.Read(ref _endOfStreamSignaled) == 1)
        {
            buffer.Clear();
            return buffer.Length;
        }

        var source = Volatile.Read(ref _source);
        if (source is null)
        {
            buffer.Clear();
            return buffer.Length;
        }

        if (source.IsEndOfStream)
        {
            buffer.Clear();
            SignalEndOfStream();
            return buffer.Length;
        }

        var read = source.Read(buffer);
        if (read >= buffer.Length)
        {
            return buffer.Length;
        }

        // Partial/final period: pad with silence so exclusive mode always releases a full buffer.
        if (read < 0)
        {
            read = 0;
        }

        buffer[read..].Clear();
        if (source.IsEndOfStream || read == 0)
        {
            SignalEndOfStream();
        }

        return buffer.Length;
    }

    private void SignalEndOfStream()
    {
        Volatile.Write(ref _endOfStreamSignaled, 1);
        if (Interlocked.Exchange(ref _eosCallbackRaised, 1) == 0)
        {
            try
            {
                _onEndOfStream?.Invoke();
            }
            catch
            {
                // Never throw from the audio callback.
            }
        }
    }
}
