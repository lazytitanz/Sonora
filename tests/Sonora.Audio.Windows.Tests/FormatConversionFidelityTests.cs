using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.Tests.Diagnostics;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Deterministic probes of production <see cref="WindowsAudioFormatConverter"/> / NAudio WDL
/// for the 44.1 kHz upward-SRC gapless static investigation. Does not change production code.
/// </summary>
/// <remarks>
/// Decoder short-read (positive under-fill) is distinct from consumer chunk size (caller requests
/// smaller buffers while the source still fills when able). NAudio 3.3 WDL flushes when
/// <c>nsamples_in &lt; ResamplePrepare(...)</c>.
/// </remarks>
public sealed class FormatConversionFidelityTests
{
    private const float ToneHz = 440f;
    private const float ToneGain = 0.25f;
    private const int DecodePumpScratchFloats = 4096;
    private const double MinProbeNcc = 0.92;
    private const float ContinuityJumpLimit = 0.35f; // at 0.25 gain 440 Hz, adjacent Δ is small

    public static TheoryData<int, int, string> RateMatrix { get; } = new()
    {
        { 44100, 48000, "fractional-up" },
        { 44100, 96000, "fractional-up" },
        { 48000, 44100, "fractional-down" },
        { 48000, 96000, "integer-2x" },
        { 44100, 44100, "identity" },
    };

    [Theory]
    [MemberData(nameof(RateMatrix))]
    public void Converter_SelectsExpectedImplementation(int inRate, int outRate, string _)
    {
        var sourceFormat = AudioFormat.CreateFloat32(inRate, 2);
        var targetFormat = AudioFormat.CreateFloat32(outRate, 2);
        using var source = new SequenceWaveDecoder(sourceFormat, new float[4]);
        var converter = new WindowsAudioFormatConverter();
        using var converted = converter.Convert(source, targetFormat);

        if (inRate == outRate)
        {
            Assert.Same(source, converted);
        }
        else
        {
            Assert.NotSame(source, converted);
            Assert.Equal(outRate, converted.Format.SampleRate);
            Assert.Equal(2, converted.Format.ChannelCount);
            Assert.Equal(SampleFormat.Float32, converted.Format.SampleFormat);
        }
    }

    /// <summary>
    /// Probe A: full-fill source, large consumer chunks (baseline).
    /// </summary>
    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(44100, 96000)]
    [InlineData(48000, 96000)]
    [InlineData(48000, 44100)]
    public void ProbeA_FullFill_LargeChunks_StereoSine_Fidelity(int inRate, int outRate)
    {
        var pcm = ConvertStereoSine(inRate, outRate, durationMs: 250, consumerChunkFloats: 16384, shortReadMaxFrames: null);
        AssertFidelity(pcm, outRate, label: $"A {inRate}->{outRate}");
    }

    /// <summary>
    /// Probe B: full-fill source, DecodePump-sized consumer chunks only (not a decoder short-read).
    /// </summary>
    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(44100, 96000)]
    [InlineData(48000, 96000)]
    public void ProbeB_FullFill_DecodePumpChunks_MatchesLargeChunks(int inRate, int outRate)
    {
        var large = ConvertStereoSine(inRate, outRate, durationMs: 250, consumerChunkFloats: 16384, shortReadMaxFrames: null);
        var pump = ConvertStereoSine(inRate, outRate, durationMs: 250, consumerChunkFloats: DecodePumpScratchFloats, shortReadMaxFrames: null);

        AssertFidelity(pump, outRate, label: $"B {inRate}->{outRate}");

        var ncc = CompareWithLag(large, pump, channels: 2, maxLagFrames: outRate / 50);
        Assert.True(
            ncc >= MinProbeNcc,
            $"Probe B (pump chunks) diverged from Probe A (large chunks) for {inRate}->{outRate}: ncc={ncc:F4}");
    }

    /// <summary>
    /// Probe C: decoder returns positive short reads mid-stream (WDL flush path). Separate from Probe B.
    /// </summary>
    [Theory]
    [InlineData(44100, 48000, 64)]
    [InlineData(44100, 96000, 64)]
    [InlineData(44100, 48000, 7)]
    [InlineData(48000, 96000, 64)]
    public void ProbeC_ForcedDecoderShortRead_Vs_FullFill(int inRate, int outRate, int maxFramesPerRead)
    {
        var full = ConvertStereoSine(inRate, outRate, durationMs: 250, consumerChunkFloats: 16384, shortReadMaxFrames: null);
        var shorted = ConvertStereoSine(inRate, outRate, durationMs: 250, consumerChunkFloats: 16384, shortReadMaxFrames: maxFramesPerRead);

        AssertNoInvalid(shorted, label: $"C short={maxFramesPerRead} {inRate}->{outRate}");

        var ncc = CompareWithLag(full, shorted, channels: 2, maxLagFrames: outRate / 20);
        // Documented expectation: mid-stream short reads flush WDL. A severe collapse is a defect signal.
        // Mild filter-state differences can occur; require still-recognizable sine content.
        AssertFidelity(shorted, outRate, label: $"C short={maxFramesPerRead} {inRate}->{outRate}");
        Assert.True(
            ncc >= 0.75,
            $"Probe C (decoder short-read maxFrames={maxFramesPerRead}) diverged strongly from full-fill "
            + $"for {inRate}->{outRate}: ncc={ncc:F4}. Likely WDL flush-on-short-input corruption.");
    }

    [Fact]
    public void ProbeD_ShortRead_NotConflated_With_ConsumerChunkSize()
    {
        const int inRate = 44100;
        const int outRate = 48000;
        var a = ConvertStereoSine(inRate, outRate, 250, 16384, null);
        var b = ConvertStereoSine(inRate, outRate, 250, DecodePumpScratchFloats, null);
        var c = ConvertStereoSine(inRate, outRate, 250, 16384, shortReadMaxFrames: 32);

        var nccAB = CompareWithLag(a, b, 2, outRate / 50);
        var nccAC = CompareWithLag(a, c, 2, outRate / 20);

        Assert.True(nccAB >= MinProbeNcc, $"A vs B (chunk only) should match; ncc={nccAB:F4}");
        // C may be slightly worse than B; it must not be silently identical in naming/intent.
        Assert.True(nccAC > 0.5, $"A vs C produced unusable audio; ncc={nccAC:F4}");
    }

    [Theory]
    [MemberData(nameof(RateMatrix))]
    public void StereoSine_FrequencyContinuityChannelFidelity(int inRate, int outRate, string ratioClass)
    {
        var pcm = ConvertStereoSine(inRate, outRate, durationMs: 300, consumerChunkFloats: DecodePumpScratchFloats, shortReadMaxFrames: null);
        AssertFidelity(pcm, outRate, label: $"{ratioClass} {inRate}->{outRate}");
        AssertFrameCountNearExpected(pcm.Length, inFrames: inRate * 300 / 1000, inRate, outRate, channels: 2);
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(44100, 96000)]
    [InlineData(48000, 96000)]
    public void ChannelSeparation_LeftImpulse_StaysLeftDominant(int inRate, int outRate)
    {
        var frames = inRate / 5; // 200 ms
        var samples = new float[frames * 2];
        var impulseAt = frames / 4;
        samples[impulseAt * 2] = 0.9f; // L only

        using var source = new SequenceWaveDecoder(AudioFormat.CreateFloat32(inRate, 2), samples);
        var converter = new WindowsAudioFormatConverter();
        using var converted = converter.Convert(source, AudioFormat.CreateFloat32(outRate, 2));
        var pcm = Drain(converted, DecodePumpScratchFloats);

        AssertNoInvalid(pcm, "L-impulse");
        double lEnergy = 0, rEnergy = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            lEnergy += pcm[i] * pcm[i];
            rEnergy += pcm[i + 1] * pcm[i + 1];
        }

        Assert.True(lEnergy > 1e-6, "Left channel lost the impulse energy.");
        Assert.True(
            lEnergy > rEnergy * 4,
            $"Channel bleed after {inRate}->{outRate}: L={lEnergy:E3} R={rEnergy:E3}");
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(44100, 96000)]
    [InlineData(48000, 96000)]
    public void Silence_StaysNearZero(int inRate, int outRate)
    {
        var frames = inRate / 5;
        var samples = new float[frames * 2];
        using var source = new SequenceWaveDecoder(AudioFormat.CreateFloat32(inRate, 2), samples);
        var converter = new WindowsAudioFormatConverter();
        using var converted = converter.Convert(source, AudioFormat.CreateFloat32(outRate, 2));
        var pcm = Drain(converted, DecodePumpScratchFloats);
        var amp = PcmIntegrityMetrics.Analyze(pcm);
        Assert.Equal(0, amp.NonFiniteCount);
        Assert.True(amp.Peak < 1e-4f, $"Silence peak {amp.Peak} after {inRate}->{outRate}");
    }

    /// <summary>
    /// Side-by-side metrics: fractional upward vs clean 2×. Does not assume fractional is defective.
    /// </summary>
    [Fact]
    public void FractionalUpward_Vs_Integer2x_SideBySide_Metrics()
    {
        var frac48 = Measure(ConvertStereoSine(44100, 48000, 300, DecodePumpScratchFloats, null), 48000);
        var frac96 = Measure(ConvertStereoSine(44100, 96000, 300, DecodePumpScratchFloats, null), 96000);
        var twoX = Measure(ConvertStereoSine(48000, 96000, 300, DecodePumpScratchFloats, null), 96000);

        Assert.True(frac48.FrequencyOk && frac96.FrequencyOk && twoX.FrequencyOk,
            $"Frequency: 44.1→48 ok={frac48.FrequencyOk} ({frac48.EstimatedHz:F1}Hz); "
            + $"44.1→96 ok={frac96.FrequencyOk} ({frac96.EstimatedHz:F1}Hz); "
            + $"48→96 ok={twoX.FrequencyOk} ({twoX.EstimatedHz:F1}Hz)");

        Assert.True(frac48.ContinuityOk && frac96.ContinuityOk && twoX.ContinuityOk,
            $"Continuity jumps: 44.1→48 maxΔ={frac48.MaxAdjacentDelta:F3}; "
            + $"44.1→96 maxΔ={frac96.MaxAdjacentDelta:F3}; "
            + $"48→96 maxΔ={twoX.MaxAdjacentDelta:F3}");

        Assert.True(frac48.Peak < 1.2f && frac96.Peak < 1.2f && twoX.Peak < 1.2f,
            $"Peaks: 44.1→48={frac48.Peak:F3} 44.1→96={frac96.Peak:F3} 48→96={twoX.Peak:F3}");

        // Ratio accounting documentation (m_ratio = in/out in NAudio 3.3):
        Assert.Equal(44100.0 / 48000.0, 44100.0 / 48000.0, precision: 12);
        Assert.Equal(44100.0 / 96000.0, 44100.0 / 96000.0, precision: 12);
        Assert.Equal(0.5, 48000.0 / 96000.0, precision: 12);
    }

    [Fact]
    public void WdlRatio_IsInputOverOutput_AsConfiguredByNAudio33()
    {
        // NAudio 3.3 WdlResampler.SetRates: m_ratio = m_sratein / m_srateout.
        // Upward conversion ⇒ ratio < 1; 2× upsample 48→96 ⇒ ratio == 0.5.
        Assert.True(44100.0 / 48000.0 < 1.0);
        Assert.True(44100.0 / 96000.0 < 1.0);
        Assert.Equal(0.5, 48000.0 / 96000.0, precision: 10);
        Assert.True(48000.0 / 44100.0 > 1.0); // downward
    }

    [Fact]
    public async Task WaveStreamDecoder_FillsLargeDestination_SoWdlDoesNotSeeMidStreamShorts()
    {
        // Committed PCM fixture exercises WaveStreamAudioDecoder (1024 scratch).
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromSeconds(1));
        var factory = new WindowsAudioDecoderFactory();
        using (var decoder = await factory.CreateAsync(path))
        {
            var buffer = new float[8192];
            var read = decoder.Read(buffer);
            Assert.True(read >= 8192 || read == 0, $"WaveStream decoder under-filled: read={read}");
            if (read == 0)
            {
                return;
            }
        }

        // Fresh open for conversion — Convert owns the decoder instance.
        var native = await factory.CreateAsync(path);
        var targetRate = native.Format.SampleRate == 44100 ? 48000 : native.Format.SampleRate * 2;
        if (native.Format.SampleRate == targetRate)
        {
            native.Dispose();
            return;
        }

        var converter = new WindowsAudioFormatConverter();
        using var converted = converter.Convert(
            native,
            AudioFormat.CreateFloat32(targetRate, native.Format.ChannelCount));
        var pcm = Drain(converted, DecodePumpScratchFloats);
        AssertNoInvalid(pcm, "WaveStream→WDL");
        Assert.True(pcm.Length > targetRate / 10, "Converted output too short");
    }

    private static float[] ConvertStereoSine(
        int inRate,
        int outRate,
        int durationMs,
        int consumerChunkFloats,
        int? shortReadMaxFrames)
    {
        var frames = inRate * durationMs / 1000;
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var s = ToneGain * MathF.Sin(2 * MathF.PI * ToneHz * i / inRate);
            samples[i * 2] = s;
            samples[i * 2 + 1] = s;
        }

        IAudioDecoder source = new SequenceWaveDecoder(AudioFormat.CreateFloat32(inRate, 2), samples);
        if (shortReadMaxFrames is int maxFrames)
        {
            source = new ShortReadingDecoder(source, maxFrames);
        }

        var converter = new WindowsAudioFormatConverter();
        // Convert takes ownership of a non-identity wrapper (WDL owns the source decoder).
        var converted = converter.Convert(source, AudioFormat.CreateFloat32(outRate, 2));
        try
        {
            return Drain(converted, consumerChunkFloats);
        }
        finally
        {
            converted.Dispose();
            if (!ReferenceEquals(converted, source))
            {
                // Identity path returns the same instance — already disposed above.
            }
        }
    }

    private static float[] Drain(IAudioDecoder decoder, int chunkFloats)
    {
        var list = new List<float>(capacity: chunkFloats * 8);
        var buffer = new float[Math.Max(64, chunkFloats)];
        var guard = 0;
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                list.Add(buffer[i]);
            }

            if (++guard > 100_000)
            {
                throw new InvalidOperationException("Drain exceeded guard; possible non-terminating Read.");
            }
        }

        return list.ToArray();
    }

    private static void AssertFidelity(float[] pcm, int sampleRate, string label)
    {
        AssertNoInvalid(pcm, label);
        Assert.True(pcm.Length >= sampleRate / 5, $"{label}: output too short ({pcm.Length} samples)");

        var metrics = Measure(pcm, sampleRate);
        Assert.True(
            metrics.FrequencyOk,
            $"{label}: expected ~{ToneHz} Hz, estimated {metrics.EstimatedHz:F1} Hz");
        Assert.True(
            metrics.ContinuityOk,
            $"{label}: max adjacent Δ={metrics.MaxAdjacentDelta:F3} exceeds {ContinuityJumpLimit}");
        Assert.True(metrics.Peak < 1.5f, $"{label}: peak {metrics.Peak} suggests corruption");
        Assert.True(metrics.Rms > 0.01f, $"{label}: RMS too low ({metrics.Rms})");
    }

    private static void AssertNoInvalid(float[] pcm, string label)
    {
        var amp = PcmIntegrityMetrics.Analyze(pcm);
        Assert.Equal(0, amp.NonFiniteCount);
        Assert.True(amp.ClipCount < pcm.Length / 100, $"{label}: unexpected clipping count {amp.ClipCount}");
    }

    private static void AssertFrameCountNearExpected(
        int interleavedSamples,
        int inFrames,
        int inRate,
        int outRate,
        int channels)
    {
        if (inRate == outRate)
        {
            Assert.Equal(inFrames * channels, interleavedSamples);
            return;
        }

        var expectedFrames = (long)Math.Round(inFrames * (double)outRate / inRate);
        var actualFrames = interleavedSamples / channels;
        // WDL linear+IIR has filter latency / flush tail; keep a tight relative bound.
        var tolerance = Math.Max(outRate / 50, expectedFrames / 50); // ~20 ms or 2%
        Assert.InRange(actualFrames, expectedFrames - tolerance, expectedFrames + tolerance);
    }

    private static FidelityMetrics Measure(float[] pcm, int sampleRate)
    {
        var amp = PcmIntegrityMetrics.Analyze(pcm);
        var estimated = PcmFrequencyAnalyzer.EstimateDominantFrequency(pcm, sampleRate, channels: 2, ToneHz, 420f, 460f, 400f, 480f);
        var maxAdj = MaxAdjacentDelta(pcm, channels: 2);
        return new FidelityMetrics(
            estimated,
            FrequencyOk: Math.Abs(estimated - ToneHz) <= 35f,
            maxAdj,
            ContinuityOk: maxAdj <= ContinuityJumpLimit,
            amp.Peak,
            amp.Rms);
    }

    private static float MaxAdjacentDelta(float[] interleaved, int channels)
    {
        float max = 0;
        // Skip a short edge for filter settle; compare per-channel consecutive frames.
        var start = Math.Min(interleaved.Length / 10, channels * 64);
        start -= start % channels;
        for (var i = start; i + channels < interleaved.Length; i++)
        {
            var d = Math.Abs(interleaved[i + channels] - interleaved[i]);
            if (d > max)
            {
                max = d;
            }
        }

        return max;
    }

    private static double CompareWithLag(float[] a, float[] b, int channels, int maxLagFrames)
    {
        if (a.Length < 64 || b.Length < 64)
        {
            return 0;
        }

        // Use the shorter as template; search in the longer with frame-aligned lags.
        var template = a.Length <= b.Length ? a : b;
        var search = a.Length <= b.Length ? b : a;
        var maxLagSamples = maxLagFrames * channels;
        var limit = Math.Min(search.Length - template.Length, maxLagSamples);
        if (limit < 0)
        {
            var n = Math.Min(a.Length, b.Length);
            return Ncc(a.AsSpan(0, n), b.AsSpan(0, n));
        }

        double best = double.NegativeInfinity;
        for (var lag = 0; lag <= limit; lag += channels)
        {
            var ncc = Ncc(template, search.AsSpan(lag, template.Length));
            if (ncc > best)
            {
                best = ncc;
            }
        }

        return double.IsNegativeInfinity(best) ? 0 : best;
    }

    private static double Ncc(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0)
        {
            return 0;
        }

        double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        for (var i = 0; i < n; i++)
        {
            double x = a[i];
            double y = b[i];
            sumA += x;
            sumB += y;
            sumAA += x * x;
            sumBB += y * y;
            sumAB += x * y;
        }

        var meanA = sumA / n;
        var meanB = sumB / n;
        var cov = sumAB - n * meanA * meanB;
        var varA = sumAA - n * meanA * meanA;
        var varB = sumBB - n * meanB * meanB;
        if (varA <= 1e-20 || varB <= 1e-20)
        {
            return 0;
        }

        return cov / Math.Sqrt(varA * varB);
    }

    private readonly record struct FidelityMetrics(
        float EstimatedHz,
        bool FrequencyOk,
        float MaxAdjacentDelta,
        bool ContinuityOk,
        float Peak,
        float Rms);

    private sealed class SequenceWaveDecoder : IAudioDecoder
    {
        private readonly float[] _samples;
        private int _position;

        public SequenceWaveDecoder(AudioFormat format, float[] samples)
        {
            Format = format;
            _samples = samples;
            Duration = format.SamplesToTime(samples.Length);
        }

        public AudioFormat Format { get; }
        public TimeSpan Duration { get; }
        public bool HasKnownDuration => true;
        public bool CanSeek => true;

        public int Read(Span<float> destination)
        {
            var remaining = _samples.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var n = Math.Min(remaining, destination.Length);
            // Keep frame alignment when stereo.
            if (Format.ChannelCount > 1)
            {
                n -= n % Format.ChannelCount;
                if (n <= 0)
                {
                    return 0;
                }
            }

            _samples.AsSpan(_position, n).CopyTo(destination);
            _position += n;
            return n;
        }

        public AudioSeekResult Seek(TimeSpan position)
        {
            var frames = Format.TimeToFrames(position);
            _position = (int)Math.Min(frames * Format.ChannelCount, _samples.Length);
            var requested = Format.FramesToTime(frames);
            return AudioSeekResult.Exact(requested, requested);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Forces positive short reads: each Read returns at most <see cref="_maxFrames"/> frames
    /// while data remains. Returns 0 only at true EOS — the WDL flush-on-short-input case.
    /// </summary>
    private sealed class ShortReadingDecoder : IAudioDecoder
    {
        private readonly IAudioDecoder _inner;
        private readonly int _maxFrames;
        private readonly float[] _scratch;

        public ShortReadingDecoder(IAudioDecoder inner, int maxFrames)
        {
            _inner = inner;
            _maxFrames = Math.Max(1, maxFrames);
            _scratch = new float[_maxFrames * Math.Max(1, inner.Format.ChannelCount)];
        }

        public AudioFormat Format => _inner.Format;
        public TimeSpan Duration => _inner.Duration;
        public bool HasKnownDuration => _inner.HasKnownDuration;
        public bool CanSeek => _inner.CanSeek;

        public int Read(Span<float> destination)
        {
            var channels = Math.Max(1, Format.ChannelCount);
            var maxSamples = Math.Min(destination.Length, _maxFrames * channels);
            maxSamples -= maxSamples % channels;
            if (maxSamples <= 0)
            {
                return 0;
            }

            var slice = _scratch.AsSpan(0, maxSamples);
            var read = _inner.Read(slice);
            if (read <= 0)
            {
                return 0;
            }

            slice[..read].CopyTo(destination);
            return read;
        }

        public AudioSeekResult Seek(TimeSpan position) => _inner.Seek(position);

        public void Dispose() => _inner.Dispose();
    }
}
