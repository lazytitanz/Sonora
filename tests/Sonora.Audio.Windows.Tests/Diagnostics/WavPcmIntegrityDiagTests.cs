using NAudio.CoreAudioApi;
using Sonora.Audio;
using Sonora.Audio.Abstractions;
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
/// Diagnostic-only: compare a local PCM16 WAV against an independent float32 reference
/// at the Sonora decoder and managed→WASAPI boundary. No playback fixes.
/// </summary>
public sealed class WavPcmIntegrityDiagTests
{
    public const string FixtureEnv = "SONORA_DIAG_PCM_REF_WAV";
    public const string FixtureFallbackEnv = "SONORA_DIAG_GAPLESS_B";
    public const string SkipMsEnv = "SONORA_DIAG_PCM_SKIP_MS";
    public const string WindowMsEnv = "SONORA_DIAG_PCM_WINDOW_MS";

    private static readonly TimeSpan DefaultSkip = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Measurement-only volume for automated PCM compares (not for listening).</summary>
    private const float MeasurementVolume = 1.0f;

    private readonly ITestOutputHelper _output;

    public WavPcmIntegrityDiagTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void IndependentReader_SkipsListChunk_AndReadsPcm16()
    {
        var path = Path.Combine(FixtureHelper.FixturesDirectory, "integrity-list-chunk.wav");
        short[] pcm = [0, 1000, -1000, 16000, -16000, 0];
        IndependentWavPcm16Reader.WritePcm16WithListChunk(path, sampleRate: 44100, channels: 2, pcm);

        var result = IndependentWavPcm16Reader.Read(path);
        Assert.Equal(44100, result.SampleRate);
        Assert.Equal(2, result.Channels);
        Assert.Equal(16, result.BitsPerSample);
        Assert.False(result.IsExtensible);
        Assert.Equal(pcm.Length, result.Samples.Length);
        for (var i = 0; i < pcm.Length; i++)
        {
            Assert.Equal(pcm[i] / 32768f, result.Samples[i], precision: 6);
        }
    }

    [Fact]
    public void IndependentReader_SupportsExtensiblePcm16_RejectsNonPcmSubtype()
    {
        var okPath = Path.Combine(FixtureHelper.FixturesDirectory, "integrity-extensible-pcm16.wav");
        short[] pcm = [1234, -1234];
        IndependentWavPcm16Reader.WriteExtensiblePcm16(okPath, 48000, 2, pcm);
        var ok = IndependentWavPcm16Reader.Read(okPath);
        Assert.True(ok.IsExtensible);
        Assert.Equal(48000, ok.SampleRate);
        Assert.Equal(pcm[0] / 32768f, ok.Samples[0], precision: 6);

        var badPath = Path.Combine(FixtureHelper.FixturesDirectory, "integrity-extensible-float.wav");
        var floatSubtype = new Guid("00000003-0000-0010-8000-00aa00389b71"); // IEEE float
        IndependentWavPcm16Reader.WriteExtensiblePcm16(badPath, 48000, 2, pcm, floatSubtype);
        var ex = Assert.Throws<NotSupportedException>(() => IndependentWavPcm16Reader.Read(badPath));
        Assert.Contains("SubFormat is not PCM", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Phase2_Decoder_MatchesIndependentReference()
    {
        if (!TryResolveFixture(out var path))
        {
            _output.WriteLine(
                $"SKIPPED: set {FixtureEnv} (or {FixtureFallbackEnv}) to a local PCM16 WAV. Do not commit the file.");
            return;
        }

        var reference = IndependentWavPcm16Reader.Read(path);
        _output.WriteLine($"source: {reference.FormatDescription}");
        _output.WriteLine(
            $"source meta: rate={reference.SampleRate} ch={reference.Channels} bits={reference.BitsPerSample} "
            + $"blockAlign={reference.BlockAlign} dataBytes={reference.DataByteLength} frames={reference.FrameCount} "
            + $"duration={reference.Duration.TotalSeconds:F3}s extensible={reference.IsExtensible}");

        var factory = new WindowsAudioDecoderFactory();
        using var decoder = await factory.CreateAsync(path);
        var decoded = ReadAll(decoder);
        var decFmt = decoder.Format;

        _output.WriteLine(
            $"decoder: rate={decFmt.SampleRate} ch={decFmt.ChannelCount} sampleFormat={decFmt.SampleFormat} "
            + $"samples={decoded.Length} frames={decoded.Length / Math.Max(1, decFmt.ChannelCount)}");

        var refAmp = PcmIntegrityMetrics.Analyze(reference.Samples);
        var decAmp = PcmIntegrityMetrics.Analyze(decoded);
        _output.WriteLine($"reference amplitude: {refAmp}");
        _output.WriteLine($"decoder amplitude: {decAmp}");

        if (refAmp.UnexpectedClipping || decAmp.UnexpectedClipping)
        {
            _output.WriteLine(
                $"CLIPPING: refClip={refAmp.ClipCount} decClip={decAmp.ClipCount} "
                + "(unexpected for PCM16→float32 /32768 scaling)");
        }

        Assert.Equal(reference.SampleRate, decFmt.SampleRate);
        Assert.Equal(reference.Channels, decFmt.ChannelCount);
        Assert.Equal(SampleFormat.Float32, decFmt.SampleFormat);

        var deltas = PcmIntegrityMetrics.CompareAligned(
            reference.Samples,
            decoded,
            PcmIntegrityMetrics.DecoderExactTolerance);

        _output.WriteLine($"decoder vs reference: {deltas}");

        var firstStage = deltas.MaxAbsDelta <= PcmIntegrityMetrics.DecoderExactTolerance
            && deltas.FirstDivergeIndex < 0
            && Math.Abs(deltas.LengthDelta) == 0
            && decAmp.NonFiniteCount == 0
            ? "none (decoder matches reference)"
            : "decoder";

        _output.WriteLine($"Phase2 firstDivergentStage={firstStage}");

        Assert.Equal(0, decAmp.NonFiniteCount);
        Assert.Equal(0, deltas.LengthDelta);
        Assert.True(
            deltas.MaxAbsDelta <= PcmIntegrityMetrics.DecoderExactTolerance && deltas.FirstDivergeIndex < 0,
            $"Decoder diverged from independent PCM16 reference. {deltas}");
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Phase3_ManagedOutput_MatchesReference_MeasurementVolume()
    {
        if (!TryResolveFixture(out var path))
        {
            _output.WriteLine(
                $"SKIPPED: set {FixtureEnv} (or {FixtureFallbackEnv}) to a local PCM16 WAV. Do not commit the file.");
            return;
        }

        var skip = ReadTimeMs(SkipMsEnv, DefaultSkip);
        var window = ReadTimeMs(WindowMsEnv, DefaultWindow);
        var reference = IndependentWavPcm16Reader.Read(path);

        _output.WriteLine($"source: {reference.FormatDescription}");
        _output.WriteLine($"measurementVolume={MeasurementVolume} (automated only; not for listening)");
        _output.WriteLine($"capture: skip={skip.TotalMilliseconds:F0}ms window={window.TotalMilliseconds:F0}ms");

        // Fresh Load/Play sessions — managed capture quality has been observed to vary run-to-run.
        const int maxAttempts = 3;
        PcmIntegrityMetrics.AlignReport? best = null;
        string? mixFormat = null;
        var identityCount = 0;
        var attemptSummaries = new List<string>(maxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var (align, mix, summary) = await CaptureAndCompareAttemptAsync(
                path,
                reference,
                skip,
                window,
                attempt);
            mixFormat ??= mix;
            attemptSummaries.Add(summary);
            _output.WriteLine($"attempt {attempt}: {summary}");

            if (align.SampleIdentity)
            {
                identityCount++;
            }

            if (best is null
                || align.PeakNcc > best.PeakNcc
                || (align.SampleIdentity && !best.SampleIdentity))
            {
                best = align;
            }

            if (align.SampleIdentity)
            {
                break;
            }
        }

        Assert.NotNull(best);
        _output.WriteLine($"attempts={maxAttempts} identityHits={identityCount}");
        _output.WriteLine($"best: {best}");

        string stage;
        string next;
        if (identityCount > 0)
        {
            stage = "none (managed output matched reference on at least one attempt)";
            next =
                "If audible distortion remains, investigate post-managed WASAPI shared MixFormat resampling "
                + $"({mixFormat}) and OS/device processing. "
                + "Note: some attempts were non-identical — capture harness skew is possible.";
        }
        else if (!best.Suitable || best.AlignmentInconclusive || !best.ContentWindowVerified)
        {
            stage = "inconclusive";
            next = best.Reason
                ?? "Could not securely align managed capture to reference across retries.";
        }
        else
        {
            stage = "managed-output (intermittent non-identity across retries)";
            next =
                "Decoder is bit-exact (Phase2). Managed boundary sometimes fails sample identity "
                + "even when content maps near ArmedAtPosition — investigate capture arm races and "
                + "provider/volume path; also check post-managed MixFormat resampling "
                + $"({mixFormat}).";
        }

        _output.WriteLine($"Phase3 firstDivergentStage={stage}");
        _output.WriteLine($"nextRecommended={next}");
        foreach (var s in attemptSummaries)
        {
            _output.WriteLine($"attemptSummary: {s}");
        }

        if (identityCount > 0)
        {
            return;
        }

        if (!best.Suitable || best.AlignmentInconclusive || !best.ContentWindowVerified)
        {
            Assert.Fail($"Managed-output integrity inconclusive after {maxAttempts} attempts. best={best}");
        }

        Assert.Fail(
            $"Managed output did not achieve sample identity in {maxAttempts} attempts. best={best}");
    }

    private async Task<(PcmIntegrityMetrics.AlignReport Align, string MixFormat, string Summary)>
        CaptureAndCompareAttemptAsync(
            string path,
            IndependentWavPcm16Reader.Result reference,
            TimeSpan skip,
            TimeSpan window,
            int attempt)
    {
        var outputs = new TrackingWindowsOutputFactory();
        using var engine = CreateEngine(outputs);
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = MeasurementVolume;

        await player.LoadAsync(path);
        player.Play();

        var captured = await ManagedOutputPositionCapture.CaptureWindowAtPositionAsync(
            player,
            () => outputs.LastWasapi,
            skip,
            window,
            timeout: TimeSpan.FromSeconds(skip.TotalSeconds + window.TotalSeconds + 30));

        player.Stop();

        var managedFmt = captured.Format;
        var mixFormat = TryReadMixFormat(captured.EndpointId);
        if (attempt == 1)
        {
            _output.WriteLine(
                $"managed-output: rate={managedFmt.SampleRate} ch={managedFmt.ChannelCount} "
                + $"sampleFormat={managedFmt.SampleFormat}");
            _output.WriteLine($"WASAPI MixFormat: {mixFormat}");
            ReportConversionStage(reference, managedFmt, mixFormat);
        }

        _output.WriteLine(
            $"attempt {attempt} session: mode={captured.OutputMode} "
            + $"armedAt={captured.ArmedAtPosition.TotalMilliseconds:F1}ms "
            + $"samples={captured.Samples.Length} silence={captured.LastSilenceReason} "
            + $"underruns={captured.UnderrunCount}");

        var capAmp = PcmIntegrityMetrics.Analyze(captured.Samples);
        _output.WriteLine($"attempt {attempt} amplitude: {capAmp}");
        if (capAmp.UnexpectedClipping)
        {
            _output.WriteLine($"CLIPPING: attempt {attempt} has {capAmp.ClipCount} samples with |x|>1.0");
        }

        if (managedFmt.SampleRate != reference.SampleRate
            || managedFmt.ChannelCount != reference.Channels)
        {
            var rateMismatch = new PcmIntegrityMetrics.AlignReport
            {
                Suitable = false,
                Reason =
                    $"Rate/channel conversion: source {reference.SampleRate}/{reference.Channels} "
                    + $"vs managed {managedFmt.SampleRate}/{managedFmt.ChannelCount}",
            };
            return (rateMismatch, mixFormat, rateMismatch.Reason!);
        }

        var searchPad = TimeSpan.FromSeconds(2);
        var searchStart = captured.ArmedAtPosition - searchPad;
        if (searchStart < TimeSpan.Zero)
        {
            searchStart = TimeSpan.Zero;
        }

        var searchRegion = PcmIntegrityMetrics.SliceByTime(
            reference.Samples,
            reference.SampleRate,
            reference.Channels,
            searchStart,
            window + searchPad + searchPad);

        var wrong = PcmIntegrityMetrics.SliceByTime(
            reference.Samples,
            reference.SampleRate,
            reference.Channels,
            captured.ArmedAtPosition + TimeSpan.FromSeconds(5),
            window);

        var align = PcmIntegrityMetrics.FindInReferenceAndCompare(
            captured.Samples,
            searchRegion,
            wrong,
            managedFmt.SampleRate,
            managedFmt.ChannelCount,
            identityTolerance: PcmIntegrityMetrics.ManagedIdentityTolerance);

        var matchedSource =
            searchStart
            + TimeSpan.FromSeconds(
                align.BestLagSamples / (double)(managedFmt.SampleRate * managedFmt.ChannelCount));
        var armedDeltaMs = (matchedSource - captured.ArmedAtPosition).TotalMilliseconds;

        if (align.Suitable && align.PeakNcc < 0.999)
        {
            var global = PcmIntegrityMetrics.FindInReferenceAndCompare(
                captured.Samples,
                reference.Samples,
                wrong,
                managedFmt.SampleRate,
                managedFmt.ChannelCount,
                identityTolerance: PcmIntegrityMetrics.ManagedIdentityTolerance);
            var globalSource = TimeSpan.FromSeconds(
                global.BestLagSamples / (double)(managedFmt.SampleRate * managedFmt.ChannelCount));
            _output.WriteLine($"attempt {attempt} full-ref: ncc={global.PeakNcc:F4} at {globalSource.TotalMilliseconds:F1}ms maxΔ={global.MaxAbsDelta:E3}");

            if (global.PeakNcc > align.PeakNcc)
            {
                align = global;
                matchedSource = globalSource;
                armedDeltaMs = (matchedSource - captured.ArmedAtPosition).TotalMilliseconds;
            }
        }

        var contentNearArmed = Math.Abs(armedDeltaMs) <= searchPad.TotalMilliseconds + 250;
        if (align.Suitable && !align.AlignmentInconclusive && !contentNearArmed)
        {
            align = align with
            {
                ContentWindowVerified = false,
                Reason =
                    $"Aligned content maps to {matchedSource.TotalMilliseconds:F0}ms but armedAt="
                    + $"{captured.ArmedAtPosition.TotalMilliseconds:F0}ms (delta {armedDeltaMs:F0}ms).",
            };
        }

        var summary =
            $"ncc={align.PeakNcc:F4} identity={align.SampleIdentity} maxΔ={align.MaxAbsDelta:E3} "
            + $"armedΔ={armedDeltaMs:F1}ms contentOk={align.ContentWindowVerified} clip={capAmp.ClipCount}";
        return (align, mixFormat, summary);
    }

    private void ReportConversionStage(
        IndependentWavPcm16Reader.Result reference,
        AudioFormat managed,
        string mixFormat)
    {
        if (managed.SampleRate == reference.SampleRate && managed.ChannelCount == reference.Channels)
        {
            _output.WriteLine(
                "format: source rate/ch == managed session (no Sonora resample observed at managed boundary).");
        }
        else
        {
            _output.WriteLine(
                $"format: conversion observed before/at managed boundary — "
                + $"source {reference.SampleRate}/{reference.Channels} → managed {managed.SampleRate}/{managed.ChannelCount}.");
        }

        _output.WriteLine($"format: MixFormat probe = {mixFormat}");
    }

    private static string TryReadMixFormat(string? endpointId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = string.IsNullOrWhiteSpace(endpointId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(endpointId);

            using var client = device.CreateAudioClient();
            var mix = client.MixFormat;
            return $"{mix.SampleRate}Hz {mix.Channels}ch encoding={mix.Encoding} bits={mix.BitsPerSample}";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static float[] ReadAll(IAudioDecoder decoder)
    {
        var buffer = new float[Math.Max(1, decoder.Format.ChannelCount) * 4096];
        var list = new List<float>(capacity: 1 << 20);
        int n;
        while ((n = decoder.Read(buffer)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                list.Add(buffer[i]);
            }
        }

        return list.ToArray();
    }

    private static bool TryResolveFixture(out string path)
    {
        path = Environment.GetEnvironmentVariable(FixtureEnv)
            ?? Environment.GetEnvironmentVariable(FixtureFallbackEnv)
            ?? "";
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static AudioEngine CreateEngine(TrackingWindowsOutputFactory outputs) =>
        new(
            new WindowsAudioDecoderFactory(),
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.FromMilliseconds(50),
                BufferDuration = TimeSpan.FromMilliseconds(500),
                EffectFactory = new WindowsAudioEffectFactory(),
                FormatConverter = new WindowsAudioFormatConverter(),
            },
            new WindowsAudioOutputDevices());

    private static TimeSpan ReadTimeMs(string envName, TimeSpan fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw) || !double.TryParse(raw, out var ms) || ms < 0)
        {
            return fallback;
        }

        return TimeSpan.FromMilliseconds(ms);
    }
}
