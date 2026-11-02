using NAudio.Wave;
using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Adapts <see cref="IPcmSampleSource"/> to NAudio's <see cref="ISampleProvider"/>.
/// Reads only buffered PCM — no decoding or disk I/O.
/// </summary>
/// <remarks>
/// WASAPI exclusive event-driven mode requires each render period to be fully written.
/// A short read at end-of-stream causes <c>AUDCLNT_E_BUFFER_SIZE_ERROR</c> (0x88890016)
/// in <c>AudioRenderClient.ReleaseBuffer</c>. This adapter pads the final period with silence
/// and keeps returning silence afterward until the output stops the WASAPI client — it must
/// never return a short/zero period while the client is running, and must not treat pause
/// underruns as end-of-stream.
/// </remarks>
internal sealed class PcmSampleProviderAdapter : ISampleProvider
{
    private IPcmSampleSource? _source;
    private readonly WaveFormat _waveFormat;
    private readonly Action? _onEndOfStream;
    private int _endOfStreamSignaled;
    private int _eosCallbackRaised;
    private int _paused;

    public PcmSampleProviderAdapter(IPcmSampleSource source, Action? onEndOfStream = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _onEndOfStream = onEndOfStream;
        var format = source.Format;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.ChannelCount);
    }

    public WaveFormat WaveFormat => _waveFormat;

    /// <summary>True after a genuine source EOS has been observed and padded.</summary>
    public bool HasSignaledEndOfStream => Volatile.Read(ref _endOfStreamSignaled) == 1;

    /// <summary>
    /// Prevents any further reads of the PCM source (used during output shutdown).
    /// </summary>
    public void Disconnect() => Volatile.Write(ref _source, null);

    /// <summary>
    /// Clears EOS latch so a restarted WASAPI play thread can stream again after an intentional
    /// Stop used for exclusive-mode pause (same adapter instance, mid-track resume).
    /// </summary>
    public void ResetEndOfStreamLatch()
    {
        Volatile.Write(ref _endOfStreamSignaled, 0);
        Volatile.Write(ref _eosCallbackRaised, 0);
    }

    /// <summary>
    /// When paused, exclusive event-driven WASAPI must still receive full periods. Feeding silence
    /// keeps <c>IAudioClient</c> running without repeating the last audible period (NAudio's soft
    /// Pause skips FillBuffer and leaves the last exclusive buffer looping on many devices).
    /// </summary>
    public void SetPaused(bool paused) => Volatile.Write(ref _paused, paused ? 1 : 0);

    public int Read(Span<float> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Exclusive pause: full silent periods while the WASAPI client remains started.
        if (Volatile.Read(ref _paused) == 1)
        {
            buffer.Clear();
            return buffer.Length;
        }

        // After genuine EOS, keep returning full silent periods until the output stops the client.
        // Returning 0 here would ReleaseBuffer(0) → AUDCLNT_E_BUFFER_SIZE_ERROR in exclusive mode.
        if (Volatile.Read(ref _endOfStreamSignaled) == 1)
        {
            buffer.Clear();
            return buffer.Length;
        }

        var source = Volatile.Read(ref _source);
        if (source is null)
        {
            // Disconnected during shutdown: feed silence until Stop joins the render thread.
            // Do not treat this as track EOS (would confuse pause/shutdown with completion).
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

        if (read < 0)
        {
            read = 0;
        }

        // Always pad to a full period while the WASAPI client is running.
        buffer[read..].Clear();

        // Only latch EOS when the source itself reports completion — never on a transient
        // zero/short read (underrun / pause race), which would stop playback incorrectly.
        if (source.IsEndOfStream)
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
