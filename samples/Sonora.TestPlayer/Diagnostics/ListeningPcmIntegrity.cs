namespace Sonora.TestPlayer.Diagnostics;

/// <summary>
/// Volume-aware sample-exact alignment of a managed capture against a unit-scale WAV reference.
/// Does not infer audible quality from metrics.
/// </summary>
internal static class ListeningPcmIntegrity
{
    public const float IdentityTolerance = 2f / 32768f;
    public const double MinNcc = 0.85;

    public sealed record Report
    {
        public bool Suitable { get; init; }
        public string? Reason { get; init; }
        public bool AlignmentInconclusive { get; init; }
        public bool SampleIdentity { get; init; }
        public double PeakNcc { get; init; }
        public int BestOffsetSamples { get; init; }
        public double MatchedSourceMs { get; init; }
        public double ArmedAtMs { get; init; }
        public double ArmedDeltaMs { get; init; }
        public float MaxAbsDelta { get; init; }
        public float RmsDelta { get; init; }
        public float ListeningVolume { get; init; }
        public string Summary { get; init; } = "inconclusive";
    }

    /// <summary>
    /// Searches <paramref name="unitReference"/> for <paramref name="capture"/> after scaling the
    /// reference by <paramref name="listeningVolume"/> (TestPlayer volume, not forced to 1.0).
    /// </summary>
    public static Report CompareCaptureToReference(
        float[] capture,
        float[] unitReference,
        int sampleRate,
        int channels,
        TimeSpan armedAt,
        float listeningVolume)
    {
        channels = Math.Max(1, channels);
        listeningVolume = Math.Clamp(listeningVolume, 0f, 1f);
        if (capture.Length < 64 || unitReference.Length < capture.Length)
        {
            return new Report
            {
                Suitable = false,
                Reason = "Capture too short or reference shorter than capture.",
                ListeningVolume = listeningVolume,
                ArmedAtMs = armedAt.TotalMilliseconds,
                Summary = "inconclusive",
            };
        }

        var capRms = Rms(capture);
        if (capRms < 1e-4f * Math.Max(0.05f, listeningVolume))
        {
            return new Report
            {
                Suitable = false,
                Reason = "Capture RMS below silence floor (for listening volume).",
                ListeningVolume = listeningVolume,
                ArmedAtMs = armedAt.TotalMilliseconds,
                Summary = "inconclusive-silence",
            };
        }

        // Coarse then fine frame-aligned NCC of capture vs volume*reference.
        var scale = listeningVolume <= 0 ? 1f : listeningVolume;
        var limit = unitReference.Length - capture.Length;
        var coarse = channels * 64;
        var bestOff = 0;
        var bestNcc = double.NegativeInfinity;
        for (var off = 0; off <= limit; off += coarse)
        {
            var ncc = NccScaled(capture, unitReference.AsSpan(off, capture.Length), scale);
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestOff = off;
            }
        }

        var fineStart = Math.Max(0, bestOff - coarse * 2);
        fineStart -= fineStart % channels;
        var fineEnd = Math.Min(limit, bestOff + coarse * 2);
        for (var off = fineStart; off <= fineEnd; off += channels)
        {
            var ncc = NccScaled(capture, unitReference.AsSpan(off, capture.Length), scale);
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestOff = off;
            }
        }

        // Dense polish near best (±50 ms).
        var polish = sampleRate * channels / 20;
        fineStart = Math.Max(0, bestOff - polish);
        fineStart -= fineStart % channels;
        fineEnd = Math.Min(limit, bestOff + polish);
        for (var off = fineStart; off <= fineEnd; off += channels)
        {
            var ncc = NccScaled(capture, unitReference.AsSpan(off, capture.Length), scale);
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestOff = off;
            }
        }

        float max = 0;
        double sumSq = 0;
        var first = -1;
        for (var i = 0; i < capture.Length; i++)
        {
            var expected = unitReference[bestOff + i] * scale;
            var d = Math.Abs(capture[i] - expected);
            if (d > max)
            {
                max = d;
            }

            if (first < 0 && d > IdentityTolerance)
            {
                first = i;
            }

            sumSq += d * d;
        }

        var matchedMs = bestOff / (double)(sampleRate * channels) * 1000.0;
        var armedDelta = matchedMs - armedAt.TotalMilliseconds;
        var alignmentOk = bestNcc >= MinNcc;
        var identity = alignmentOk && max <= IdentityTolerance && first < 0;
        string summary;
        if (!alignmentOk)
        {
            summary = "inconclusive";
        }
        else if (identity)
        {
            summary = "PCM exact";
        }
        else
        {
            summary = "PCM differs";
        }

        return new Report
        {
            Suitable = true,
            AlignmentInconclusive = !alignmentOk,
            SampleIdentity = identity,
            PeakNcc = bestNcc,
            BestOffsetSamples = bestOff,
            MatchedSourceMs = matchedMs,
            ArmedAtMs = armedAt.TotalMilliseconds,
            ArmedDeltaMs = armedDelta,
            MaxAbsDelta = max,
            RmsDelta = (float)Math.Sqrt(sumSq / capture.Length),
            ListeningVolume = listeningVolume,
            Summary = summary,
            Reason = !alignmentOk
                ? $"Best NCC {bestNcc:F4} below {MinNcc:F2} — inconclusive (not inferred as corruption)."
                : null,
        };
    }

    private static double NccScaled(ReadOnlySpan<float> capture, ReadOnlySpan<float> unitRef, float scale)
    {
        var n = Math.Min(capture.Length, unitRef.Length);
        double meanA = 0, meanB = 0;
        for (var i = 0; i < n; i++)
        {
            meanA += capture[i];
            meanB += unitRef[i] * scale;
        }

        meanA /= n;
        meanB /= n;
        double num = 0, denA = 0, denB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = capture[i] - meanA;
            var db = unitRef[i] * scale - meanB;
            num += da * db;
            denA += da * da;
            denB += db * db;
        }

        var den = Math.Sqrt(denA * denB);
        return den < 1e-20 ? 0 : num / den;
    }

    private static float Rms(ReadOnlySpan<float> s)
    {
        if (s.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < s.Length; i++)
        {
            sum += s[i] * s[i];
        }

        return (float)Math.Sqrt(sum / s.Length);
    }
}
