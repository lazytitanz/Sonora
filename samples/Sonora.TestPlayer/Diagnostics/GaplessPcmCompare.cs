using System.Globalization;
using System.Text.Json;

namespace Sonora.TestPlayer.Diagnostics;

/// <summary>Offline NCC alignment + metrics for promote vs manual managed-boundary dumps.</summary>
public static class GaplessPcmCompare
{
    public static object CompareDirectories(string promoteDir, string manualDir, int maxLagMs)
    {
        var promoteMeta = ReadMeta(Path.Combine(promoteDir, "promote.json"));
        var manualMeta = ReadMeta(Path.Combine(manualDir, "manual.json"));
        var promote = ReadFloats(Path.Combine(promoteDir, "promote.f32"));
        var manual = ReadFloats(Path.Combine(manualDir, "manual.f32"));

        var sampleRate = promoteMeta.GetProperty("sampleRate").GetInt32();
        var channels = promoteMeta.GetProperty("channelCount").GetInt32();
        if (sampleRate != manualMeta.GetProperty("sampleRate").GetInt32()
            || channels != manualMeta.GetProperty("channelCount").GetInt32())
        {
            return new
            {
                suitable = false,
                reason = "Sample rate or channel count mismatch between captures.",
                promoteDir,
                manualDir,
            };
        }

        return Compare(promote, manual, sampleRate, channels, maxLagMs, promoteDir, manualDir, promoteMeta, manualMeta);
    }

    public static object Compare(
        float[] promote,
        float[] manual,
        int sampleRate,
        int channels,
        int maxLagMs,
        string? promoteDir = null,
        string? manualDir = null,
        JsonElement? promoteMeta = null,
        JsonElement? manualMeta = null)
    {
        channels = Math.Max(1, channels);
        var promoteRms = Rms(promote);
        var manualRms = Rms(manual);
        var silenceFloor = 1e-4f;

        if (promote.Length < sampleRate * channels / 10
            || manual.Length < sampleRate * channels / 10)
        {
            return new
            {
                suitable = false,
                reason = "Capture too short for reliable alignment.",
                promoteSamples = promote.Length,
                manualSamples = manual.Length,
                promoteDir,
                manualDir,
            };
        }

        if (promoteRms < silenceFloor || manualRms < silenceFloor)
        {
            return new
            {
                suitable = false,
                reason = "Capture RMS below silence floor; fixture section may be silent.",
                promoteRms,
                manualRms,
                promoteDir,
                manualDir,
            };
        }

        var maxLagSamples = (int)(sampleRate * (maxLagMs / 1000.0) * channels);
        maxLagSamples -= maxLagSamples % channels;
        maxLagSamples = Math.Max(channels, maxLagSamples);

        var (bestLag, bestNcc) = FindBestLag(promote, manual, channels, maxLagSamples);
        if (!TryOverlap(promote, manual, bestLag, out var p, out var m))
        {
            return new
            {
                suitable = false,
                reason = "Could not form an overlap at the best lag.",
                bestLagSamples = bestLag,
                promoteDir,
                manualDir,
            };
        }

        // Weak correlation on non-periodic audio ⇒ do not claim a match.
        const double minNcc = 0.85;
        var metrics = Measure(p, m, channels);
        var alignedMatch = bestNcc >= minNcc
            && metrics.MaxAbsDelta < 1e-4f
            && metrics.FirstDivergeIndex < 0;

        return new
        {
            suitable = true,
            alignmentInconclusive = bestNcc < minNcc,
            alignedMatch,
            bestLagSamples = bestLag,
            bestLagFrames = bestLag / channels,
            bestLagMs = bestLag / (double)(sampleRate * channels) * 1000.0,
            peakNcc = bestNcc,
            comparedSamples = metrics.WindowSamples,
            comparedFrames = metrics.WindowSamples / channels,
            maxAbsDelta = metrics.MaxAbsDelta,
            rmsDelta = metrics.RmsDelta,
            promoteRms = metrics.PromoteRms,
            manualRms = metrics.ManualRms,
            promotePeak = Peak(p),
            manualPeak = Peak(m),
            nonFinitePromote = CountNonFinite(p),
            nonFiniteManual = CountNonFinite(m),
            nearClipPromote = CountNearClip(p),
            nearClipManual = CountNearClip(m),
            stereoLrProductMean = metrics.StereoLrProductMean,
            firstDivergeIndex = metrics.FirstDivergeIndex,
            promoteDir,
            manualDir,
            promoteMeta,
            manualMeta,
            note = bestNcc < minNcc
                ? "Peak NCC below threshold; do not treat as identical PCM."
                : alignedMatch
                    ? "Aligned PCM matches within epsilon at managed WASAPI boundary."
                    : "Aligned streams still diverge after best lag.",
        };
    }

    private static (int Lag, double Ncc) FindBestLag(
        ReadOnlySpan<float> promote,
        ReadOnlySpan<float> manual,
        int channels,
        int maxLagSamples)
    {
        var bestLag = 0;
        var bestNcc = double.NegativeInfinity;

        for (var lag = -maxLagSamples; lag <= maxLagSamples; lag += channels)
        {
            if (!TryOverlap(promote, manual, lag, out var p, out var m))
            {
                continue;
            }

            var ncc = NormalizedCrossCorrelation(p, m);
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestLag = lag;
            }
        }

        return (bestLag, bestNcc);
    }

    private static double NormalizedCrossCorrelation(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 64)
        {
            return double.NegativeInfinity;
        }

        double meanA = 0, meanB = 0;
        for (var i = 0; i < n; i++)
        {
            meanA += a[i];
            meanB += b[i];
        }

        meanA /= n;
        meanB /= n;

        double num = 0, denA = 0, denB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            num += da * db;
            denA += da * da;
            denB += db * db;
        }

        var den = Math.Sqrt(denA * denB);
        return den < 1e-20 ? 0 : num / den;
    }

    private static bool TryOverlap(
        ReadOnlySpan<float> promote,
        ReadOnlySpan<float> manual,
        int lag,
        out ReadOnlySpan<float> p,
        out ReadOnlySpan<float> m)
    {
        if (lag >= 0)
        {
            if (lag >= promote.Length || promote.Length - lag < 64 || manual.Length < 64)
            {
                p = default;
                m = default;
                return false;
            }

            var n = Math.Min(promote.Length - lag, manual.Length);
            p = promote.Slice(lag, n);
            m = manual[..n];
            return n >= 64;
        }

        var skipM = -lag;
        if (skipM >= manual.Length || manual.Length - skipM < 64 || promote.Length < 64)
        {
            p = default;
            m = default;
            return false;
        }

        var n2 = Math.Min(promote.Length, manual.Length - skipM);
        p = promote[..n2];
        m = manual.Slice(skipM, n2);
        return n2 >= 64;
    }

    private static Metrics Measure(ReadOnlySpan<float> promote, ReadOnlySpan<float> manual, int channels)
    {
        var n = Math.Min(promote.Length, manual.Length);
        double sumSq = 0, pSq = 0, mSq = 0, lr = 0;
        float max = 0;
        var first = -1;
        const float epsilon = 1e-4f;

        for (var i = 0; i < n; i++)
        {
            var d = Math.Abs(promote[i] - manual[i]);
            if (d > max)
            {
                max = d;
            }

            if (first < 0 && d > epsilon)
            {
                first = i;
            }

            sumSq += d * d;
            pSq += promote[i] * promote[i];
            mSq += manual[i] * manual[i];
        }

        if (channels >= 2)
        {
            var frames = n / channels;
            for (var f = 0; f < frames; f++)
            {
                var i = f * channels;
                lr += promote[i] * promote[i + 1];
            }

            lr = frames > 0 ? lr / frames : 0;
        }

        return new Metrics(
            max,
            n > 0 ? (float)Math.Sqrt(sumSq / n) : 0,
            n > 0 ? (float)Math.Sqrt(pSq / n) : 0,
            n > 0 ? (float)Math.Sqrt(mSq / n) : 0,
            first,
            n,
            (float)lr);
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var a = Math.Abs(samples[i]);
            if (a > peak)
            {
                peak = a;
            }
        }

        return peak;
    }

    private static int CountNonFinite(ReadOnlySpan<float> samples)
    {
        var n = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            if (!float.IsFinite(samples[i]))
            {
                n++;
            }
        }

        return n;
    }

    private static int CountNearClip(ReadOnlySpan<float> samples)
    {
        var n = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) >= 0.999f)
            {
                n++;
            }
        }

        return n;
    }

    private static JsonElement ReadMeta(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static float[] ReadFloats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException($"Float dump length is not a multiple of 4: {path}");
        }

        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private readonly record struct Metrics(
        float MaxAbsDelta,
        float RmsDelta,
        float PromoteRms,
        float ManualRms,
        int FirstDivergeIndex,
        int WindowSamples,
        float StereoLrProductMean);
}
