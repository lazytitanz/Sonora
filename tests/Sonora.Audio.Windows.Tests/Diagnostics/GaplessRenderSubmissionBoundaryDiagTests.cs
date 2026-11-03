using NAudio.Wave;
using Sonora.Audio;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.Devices;
using Sonora.Audio.Windows.DSP;
using Sonora.Audio.Windows.Output;
using Sonora.Audio.Windows.Tests.Fakes;
using Xunit.Abstractions;

namespace Sonora.Audio.Windows.Tests.Diagnostics;

/// <summary>
/// Hardware diagnostic: compare render-client submission PCM (pre-ReleaseBuffer / pre-engine)
/// across a verified natural gapless promote against expected A‖B stereo constants.
/// Distinguishes managed-provider capture from submission-probe capture; does not claim
/// MixFormat / APO / driver observability.
/// </summary>
public sealed class GaplessRenderSubmissionBoundaryDiagTests
{
    private const float AL = 0.5f;
    private const float AR = -0.5f;
    private const float BL = 0.25f;
    private const float BR = -0.25f;

    private readonly ITestOutputHelper _output;

    public GaplessRenderSubmissionBoundaryDiagTests(ITestOutputHelper output) => _output = output;

    [HardwareFact]
    [Trait("Category", "Hardware")]
    [Trait("Category", "Diagnostic")]
    public async Task PromoteSeam_RenderSubmissionBytes_MatchExpectedConcatenation()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var aFrames = 4410; // 100 ms
        var bFrames = 8820; // 200 ms
        var aSamples = CreateStereo(aFrames, AL, AR);
        var bSamples = CreateStereo(bFrames, BL, BR);
        var expected = Concat(aSamples, bSamples);

        var aPath = WriteFloatWav("render-sub-a.wav", format, aSamples);
        var bPath = WriteFloatWav("render-sub-b.wav", format, bSamples);

        // Capture a window spanning the seam: last 256 frames of A + first 256 of B.
        const int seamFrames = 256;
        var skipSamples = (aFrames - seamFrames) * 2;
        var captureSamples = seamFrames * 2 * 2;
        var expectedWindow = expected.AsSpan(skipSamples, captureSamples).ToArray();

        var outputs = new TrackingWindowsOutputFactory();
        using var engine = CreateEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = 1f;

        var transitions = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackCompleted += (_, _) => completed.TrySetResult();

        await player.LoadAsync(aPath);
        await player.PreloadNextAsync(bPath);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);

        var output = outputs.LastWasapi
            ?? throw new InvalidOperationException("Expected WASAPI output after Load.");

        // Arm both boundaries with identical windows before Play.
        output.ArmManagedOutputCapture(skipSamples, captureSamples);
        output.ArmRenderSubmissionCapture(skipSamples, captureSamples);

        var clientFmt = FormatString(output.ClientOutputWaveFormat);
        var mixFmt = FormatString(output.DeviceMixFormat);
        _output.WriteLine($"ClientOutputWaveFormat={clientFmt}");
        _output.WriteLine($"DeviceMixFormat={mixFmt}");
        _output.WriteLine(
            "Note: submission probe observes IAudioRenderClient lease bytes before ReleaseBuffer. "
            + "Shared-mode AutoConvertPcm / mixer / APOs / driver remain unobserved.");

        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((!output.IsRenderSubmissionCaptureComplete
                || !output.IsManagedOutputCaptureComplete)
               && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
        }

        Assert.True(
            output.IsRenderSubmissionCaptureComplete,
            "Render-submission capture did not complete (pre-engine lease probe).");
        Assert.True(
            output.IsManagedOutputCaptureComplete,
            "Managed-provider capture did not complete.");

        var submission = new float[captureSamples];
        var managed = new float[captureSamples];
        Assert.True(output.TryCopyRenderSubmissionCapture(submission, out var subWritten));
        Assert.True(output.TryCopyManagedOutputCapture(managed, out var manWritten));
        Assert.Equal(captureSamples, subWritten);
        Assert.Equal(captureSamples, manWritten);

        // Provider vs submission: SampleToWaveProvider is a float↔byte cast into the lease —
        // these must match. If they diverge, something unexpected sits between them.
        var providerVsSubmission = MaxAbsDelta(managed, submission);
        _output.WriteLine($"managedProviderVsSubmission maxΔ={providerVsSubmission:E3}");

        var vsExpected = MaxAbsDelta(submission, expectedWindow);
        var firstDiv = FirstDiverge(submission, expectedWindow);
        _output.WriteLine(
            $"submissionVsExpectedA||B maxΔ={vsExpected:E3} firstDiv={firstDiv} "
            + $"skipSamples={skipSamples} captureSamples={captureSamples}");

        var journal = output.SnapshotRenderSubmissionJournal();
        var shortReads = journal.Count(r => r.ShortRead);
        var emptyReads = journal.Count(r => r.EmptyRead);
        var oddFloats = journal.Count(r => r.OddInterleavedFloatCount);
        var frameMismatch = journal.Count(r => r.FramesReturned != r.FramesRequested && !r.EmptyRead);
        _output.WriteLine(
            $"journal periods={journal.Length} shortReads={shortReads} emptyReads={emptyReads} "
            + $"oddFloatPeriods={oddFloats} frameReqNeRet={frameMismatch}");
        if (journal.Length > 0)
        {
            var sample = journal[^1];
            _output.WriteLine(
                $"lastPeriod seq={sample.Sequence} bytes={sample.BytesReturned}/{sample.BytesRequested} "
                + $"frames={sample.FramesReturned}/{sample.FramesRequested} align={sample.BlockAlign}");
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref transitions));

        Assert.True(
            providerVsSubmission == 0f,
            $"Managed-provider capture diverged from render-submission lease floats (Δ={providerVsSubmission}). "
            + "Unexpected mutation between ISampleProvider.Read and IWaveProvider.Read into the lease.");

        Assert.True(
            vsExpected == 0f,
            $"Render-submission PCM around promote diverged from expected A‖B (Δ={vsExpected}, firstDiv={firstDiv}). "
            + "First observable divergence is at the IAudioRenderClient submission probe "
            + "(still pre-engine).");

        _output.WriteLine(
            "VERDICT: render-client submission bytes are bit-exact for the promote seam window. "
            + "Remaining unobserved: WASAPI shared AutoConvertPcm to MixFormat, audio engine mixer, "
            + "APOs, and the driver/hardware path.");
    }

    private static AudioEngine CreateEngine(TrackingWindowsOutputFactory outputs) =>
        new(
            new WindowsAudioDecoderFactory(),
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                BufferDuration = TimeSpan.FromMilliseconds(200),
                EffectFactory = new WindowsAudioEffectFactory(),
                FormatConverter = new WindowsAudioFormatConverter(),
            },
            new WindowsAudioOutputDevices());

    private static float[] CreateStereo(int frames, float left, float right)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[i * 2] = left;
            samples[i * 2 + 1] = right;
        }

        return samples;
    }

    private static float[] Concat(float[] a, float[] b)
    {
        var all = new float[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all;
    }

    private static string WriteFloatWav(string fileName, AudioFormat format, float[] samples)
    {
        var dir = Path.Combine(FixtureHelper.FixturesDirectory, "render-submission");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.ChannelCount);
        using var writer = new WaveFileWriter(path, waveFormat);
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static float MaxAbsDelta(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var max = 0f;
        for (var i = 0; i < n; i++)
        {
            var d = Math.Abs(a[i] - b[i]);
            if (d > max)
            {
                max = d;
            }
        }

        return max;
    }

    private static int FirstDiverge(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return a.Length == b.Length ? -1 : n;
    }

    private static string FormatString(WaveFormat? format) =>
        format is null
            ? "(null)"
            : $"{format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit {format.Encoding} align={format.BlockAlign}";
}
