using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Diagnostic pass-through over the <see cref="IWaveProvider"/> passed to
/// <c>WasapiPlayer.Init</c>. Sits after <c>SampleToWaveProvider</c> and before
/// <c>WasapiPlayer.FillBuffer</c> releases the <c>IAudioRenderClient</c> lease.
/// </summary>
/// <remarks>
/// <para>
/// NAudio's FillBuffer obtains a zero-copy lease into the native render buffer, then calls
/// <c>provider.Read(lease.Buffer)</c>. With IEEE float <c>SampleToWaveProvider</c>, that Read
/// casts the lease bytes to floats and pulls <see cref="PcmSampleProviderAdapter"/> — so this
/// probe observes the exact bytes that will be released to WASAPI (pre-engine).
/// </para>
/// <para>
/// This is <strong>not</strong> a capture of Windows shared-mode MixFormat conversion, APOs,
/// or the driver. Label results as render-client submission, never as "post-engine native PCM".
/// </para>
/// <para>
/// When capture is disarmed, Read is a pure pass-through (no alloc, no logging).
/// </para>
/// </remarks>
internal sealed class WasapiRenderSubmissionProbe : IWaveProvider
{
    private readonly IWaveProvider _inner;
    private BoundedManagedPcmCapture? _capture;

    public WasapiRenderSubmissionProbe(IWaveProvider inner, int journalCapacity = 256)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Journal = new RenderSubmissionPeriodJournal(journalCapacity);
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public RenderSubmissionPeriodJournal Journal { get; }

    /// <summary>
    /// Arms capture of interleaved floats reinterpreted from submission bytes after each Read.
    /// Call off the audio callback.
    /// </summary>
    public void ArmCapture(int skipSamples, int captureSamples)
    {
        Volatile.Write(ref _capture, new BoundedManagedPcmCapture(skipSamples, captureSamples));
    }

    public void ClearCapture() => Volatile.Write(ref _capture, null);

    public BoundedManagedPcmCapture? Capture => Volatile.Read(ref _capture);

    public int Read(Span<byte> buffer)
    {
        var bytesRequested = buffer.Length;
        var bytesRead = _inner.Read(buffer);
        var blockAlign = WaveFormat.BlockAlign;
        var floatCount = bytesRead / sizeof(float);
        var oddFloats = floatCount % Math.Max(1, WaveFormat.Channels) != 0;

        Journal.Record(bytesRequested, bytesRead, blockAlign, oddFloats);

        var capture = Volatile.Read(ref _capture);
        if (capture is not null && bytesRead >= sizeof(float))
        {
            // lease.Buffer contents after SampleToWaveProvider — same memory FillBuffer releases.
            var usableBytes = bytesRead - (bytesRead % sizeof(float));
            if (usableBytes > 0)
            {
                var floats = MemoryMarshal.Cast<byte, float>(buffer[..usableBytes]);
                capture.Offer(floats);
            }
        }

        return bytesRead;
    }
}
