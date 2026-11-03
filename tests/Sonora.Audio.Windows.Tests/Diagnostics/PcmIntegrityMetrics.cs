namespace Sonora.Audio.Windows.Tests.Diagnostics;

/// <summary>Shared amplitude / alignment metrics for WAV PCM integrity diagnostics.</summary>
internal static class PcmIntegrityMetrics
{
    public const float DecoderExactTolerance = 1e-6f;
    public const float ManagedIdentityTolerance = 2f / 32768f; // ~2 LSB after float round-trip
    public const double MinNccForAlignment = 0.85;
    public const double MinNccForContentVerify = 0.75;

    public sealed class AmplitudeReport
    {
        public float Peak { get; init; }
        public float Rms { get; init; }
        public int NonFiniteCount { get; init; }
        public int ClipCount { get; init; }
        public bool UnexpectedClipping => ClipCount > 0;

        public override string ToString() =>
            $"peak={Peak:F6} rms={Rms:F6} nonfinite={NonFiniteCount} clip={ClipCount}";
    }

    public sealed class DeltaReport
    {
        public float MaxAbsDelta { get; init; }
        public float RmsDelta { get; init; }
        public int FirstDivergeIndex { get; init; }
        public int ComparedSamples { get; init; }
        public int LengthDelta { get; init; }

        public override string ToString() =>
            $"maxΔ={MaxAbsDelta:E3} rmsΔ={RmsDelta:E3} firstDiv={FirstDivergeIndex} n={ComparedSamples} lenΔ={LengthDelta}";
    }

    public sealed record AlignReport
    {
        public bool Suitable { get; init; }
        public string? Reason { get; init; }
        public bool AlignmentInconclusive { get; init; }
        public bool SampleIdentity { get; init; }
        public double PeakNcc { get; init; }
        public int BestLagSamples { get; init; }
        public double BestLagMs { get; init; }
        public float MaxAbsDelta { get; init; }
        public float RmsDelta { get; init; }
        public int FirstDivergeIndex { get; init; }
        public int ComparedSamples { get; init; }
        public float CapturePeak { get; init; }
        public float ReferencePeak { get; init; }
        public float CaptureRms { get; init; }
        public float ReferenceRms { get; init; }
        public bool ContentWindowVerified { get; init; }
        public double WrongWindowNcc { get; init; }

        public override string ToString() =>
            $"suitable={Suitable} identity={SampleIdentity} inconclusive={AlignmentInconclusive} "
            + $"ncc={PeakNcc:F4} lag={BestLagSamples}({BestLagMs:F2}ms) maxΔ={MaxAbsDelta:E3} rmsΔ={RmsDelta:E3} "
            + $"firstDiv={FirstDivergeIndex} n={ComparedSamples} "
            + $"capPeak={CapturePeak:F4} refPeak={ReferencePeak:F4} capRms={CaptureRms:F6} refRms={ReferenceRms:F6} "
            + $"contentOk={ContentWindowVerified} wrongWinNcc={WrongWindowNcc:F4}"
            + (Reason is null ? "" : $" reason={Reason}");
    }

    public static AmplitudeReport Analyze(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return new AmplitudeReport();
        }

        double sumSq = 0;
        float peak = 0;
        var nonFinite = 0;
        var clip = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i];
            if (!float.IsFinite(v))
            {
                nonFinite++;
                continue;
            }

            var a = Math.Abs(v);
            if (a > peak)
            {
                peak = a;
            }

            if (a > 1.0f)
            {
                clip++;
            }

            sumSq += v * v;
        }

        return new AmplitudeReport
        {
            Peak = peak,
            Rms = (float)Math.Sqrt(sumSq / samples.Length),
            NonFiniteCount = nonFinite,
            ClipCount = clip,
        };
    }

    public static DeltaReport CompareAligned(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float divergeThreshold)
    {
        var n = Math.Min(a.Length, b.Length);
        double sumSq = 0;
        float max = 0;
        var first = -1;
        for (var i = 0; i < n; i++)
        {
            var d = Math.Abs(a[i] - b[i]);
            if (d > max)
            {
                max = d;
            }

            if (first < 0 && d > divergeThreshold)
            {
                first = i;
            }

            sumSq += d * d;
        }

        return new DeltaReport
        {
            MaxAbsDelta = max,
            RmsDelta = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0,
            FirstDivergeIndex = first,
            ComparedSamples = n,
            LengthDelta = a.Length - b.Length,
        };
    }

    /// <summary>
    /// Slides <paramref name="capture"/> through a longer <paramref name="searchRegion"/> (template match),
    /// then checks sample identity at the best NCC offset. Compares against <paramref name="wrongWindow"/>
    /// so high NCC alone is not treated as proof of the intended source region.
    /// <see cref="AlignReport.BestLagSamples"/> is the sample offset into <paramref name="searchRegion"/>.
    /// </summary>
    public static AlignReport FindInReferenceAndCompare(
        ReadOnlySpan<float> capture,
        ReadOnlySpan<float> searchRegion,
        ReadOnlySpan<float> wrongWindow,
        int sampleRate,
        int channels,
        float identityTolerance)
    {
        channels = Math.Max(1, channels);
        var capAmp = Analyze(capture);
        var refAmp = Analyze(searchRegion);

        if (capture.Length < sampleRate * channels / 20 || searchRegion.Length < capture.Length)
        {
            return new AlignReport
            {
                Suitable = false,
                Reason = "Capture too short or search region shorter than capture.",
            };
        }

        if (capAmp.Rms < 1e-4f || refAmp.Rms < 1e-4f)
        {
            return new AlignReport { Suitable = false, Reason = "Capture or reference RMS below silence floor." };
        }

        // Coarse → medium → sample-accurate fine search (frame-aligned).
        var coarseStep = channels * 128; // ~2.9 ms @ 44.1 kHz
        var bestOffset = 0;
        var bestNcc = double.NegativeInfinity;
        var limit = searchRegion.Length - capture.Length;
        for (var offset = 0; offset <= limit; offset += coarseStep)
        {
            var ncc = Ncc(capture, searchRegion.Slice(offset, capture.Length));
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestOffset = offset;
            }
        }

        for (var pass = 0; pass < 2; pass++)
        {
            var radius = pass == 0 ? coarseStep * 2 : channels * 16;
            var step = pass == 0 ? channels * 8 : channels;
            var fineStart = Math.Max(0, bestOffset - radius);
            fineStart -= fineStart % channels;
            var fineEnd = Math.Min(limit, bestOffset + radius);
            for (var offset = fineStart; offset <= fineEnd; offset += step)
            {
                var ncc = Ncc(capture, searchRegion.Slice(offset, capture.Length));
                if (ncc > bestNcc)
                {
                    bestNcc = ncc;
                    bestOffset = offset;
                }
            }
        }

        var matchedRef = searchRegion.Slice(bestOffset, capture.Length);
        // Drop capture edges (~50 ms) for identity metrics — arm/complete boundaries can include
        // partial callbacks without implying steady-state sample corruption.
        var edge = Math.Min(capture.Length / 10, sampleRate * channels / 20);
        edge -= edge % channels;
        ReadOnlySpan<float> capIdentity = capture;
        ReadOnlySpan<float> refIdentity = matchedRef;
        if (edge * 2 + 64 < capture.Length)
        {
            capIdentity = capture.Slice(edge, capture.Length - 2 * edge);
            refIdentity = matchedRef.Slice(edge, matchedRef.Length - 2 * edge);
        }

        var deltas = CompareAligned(capIdentity, refIdentity, identityTolerance);

        var wrongNcc = 0.0;
        if (!wrongWindow.IsEmpty && wrongWindow.Length >= capture.Length)
        {
            wrongNcc = double.NegativeInfinity;
            var wLimit = wrongWindow.Length - capture.Length;
            for (var offset = 0; offset <= wLimit; offset += channels)
            {
                var ncc = Ncc(capture, wrongWindow.Slice(offset, capture.Length));
                if (ncc > wrongNcc)
                {
                    wrongNcc = ncc;
                }
            }

            if (double.IsNegativeInfinity(wrongNcc))
            {
                wrongNcc = 0;
            }
        }
        else if (!wrongWindow.IsEmpty && wrongWindow.Length >= 64)
        {
            wrongNcc = Ncc(capture[..Math.Min(capture.Length, wrongWindow.Length)], wrongWindow);
        }

        var alignmentOk = bestNcc >= MinNccForAlignment;
        var contentOk = alignmentOk
            && (wrongWindow.IsEmpty
                || bestNcc - wrongNcc >= 0.05
                || wrongNcc < MinNccForContentVerify);

        return new AlignReport
        {
            Suitable = true,
            AlignmentInconclusive = !alignmentOk,
            SampleIdentity = alignmentOk
                && contentOk
                && deltas.MaxAbsDelta <= identityTolerance
                && deltas.FirstDivergeIndex < 0,
            PeakNcc = bestNcc,
            BestLagSamples = bestOffset,
            BestLagMs = bestOffset / (double)(sampleRate * channels) * 1000.0,
            MaxAbsDelta = deltas.MaxAbsDelta,
            RmsDelta = deltas.RmsDelta,
            FirstDivergeIndex = deltas.FirstDivergeIndex,
            ComparedSamples = deltas.ComparedSamples,
            CapturePeak = capAmp.Peak,
            ReferencePeak = Analyze(matchedRef).Peak,
            CaptureRms = capAmp.Rms,
            ReferenceRms = Analyze(matchedRef).Rms,
            ContentWindowVerified = contentOk,
            WrongWindowNcc = wrongNcc,
            Reason = !contentOk && alignmentOk
                ? "High NCC but content window verification failed (aligned region may not match expected source position)."
                : null,
        };
    }

    public static float[] SliceByTime(float[] samples, int sampleRate, int channels, TimeSpan start, TimeSpan duration)
    {
        channels = Math.Max(1, channels);
        var startFrame = (int)Math.Floor(start.TotalSeconds * sampleRate);
        var frameCount = (int)Math.Ceiling(duration.TotalSeconds * sampleRate);
        if (startFrame < 0)
        {
            startFrame = 0;
        }

        var startSample = startFrame * channels;
        var count = frameCount * channels;
        if (startSample >= samples.Length)
        {
            return Array.Empty<float>();
        }

        count = Math.Min(count, samples.Length - startSample);
        count -= count % channels;
        if (count <= 0)
        {
            return Array.Empty<float>();
        }

        return samples.AsSpan(startSample, count).ToArray();
    }

    public sealed record CompetingPeak(int OffsetSamples, double OffsetMs, double Ncc);

    public enum DivergenceKind
    {
        Identity,
        InconclusiveAlignment,
        InconclusiveContent,
        PureTimeOffset,
        PossibleFrameDupOrOmit,
        ChannelSwapOrMismatch,
        AmplitudeScale,
        SampleValueCorruption,
        SilenceUnsuitable,
    }

    /// <summary>
    /// Exhaustive frame-aligned NCC in <paramref name="searchRegion"/>; reports best lag,
    /// competing peaks, full-window residual (no edge trim), and a divergence classification.
    /// High NCC alone is not treated as sample identity or as confirmed corruption.
    /// </summary>
    public static ExactAlignReport ExactAlignAndClassify(
        ReadOnlySpan<float> capture,
        ReadOnlySpan<float> searchRegion,
        TimeSpan searchRegionStart,
        TimeSpan armedAt,
        TimeSpan requestedStart,
        int sampleRate,
        int channels,
        float identityTolerance,
        int maxCompetingPeaks = 5)
    {
        channels = Math.Max(1, channels);
        var capAmp = Analyze(capture);
        if (capture.Length < 64 || searchRegion.Length < capture.Length)
        {
            return new ExactAlignReport
            {
                Suitable = false,
                Kind = DivergenceKind.SilenceUnsuitable,
                Reason = "Capture too short or search region shorter than capture.",
                ArmedAtMs = armedAt.TotalMilliseconds,
                RequestedStartMs = requestedStart.TotalMilliseconds,
                SearchRegionStartMs = searchRegionStart.TotalMilliseconds,
            };
        }

        if (capAmp.Rms < 1e-4f)
        {
            return new ExactAlignReport
            {
                Suitable = false,
                Kind = DivergenceKind.SilenceUnsuitable,
                Reason = "Capture RMS below silence floor — inconclusive (not corruption).",
                CapturePeak = capAmp.Peak,
                CaptureRms = capAmp.Rms,
                ArmedAtMs = armedAt.TotalMilliseconds,
                RequestedStartMs = requestedStart.TotalMilliseconds,
                SearchRegionStartMs = searchRegionStart.TotalMilliseconds,
            };
        }

        var limit = searchRegion.Length - capture.Length;
        var bestOffset = 0;
        var bestNcc = double.NegativeInfinity;
        // Coarse then exhaustive fine around top candidates.
        var coarseStep = channels * 64;
        var coarseHits = new List<(int Offset, double Ncc)>();
        for (var offset = 0; offset <= limit; offset += coarseStep)
        {
            var ncc = Ncc(capture, searchRegion.Slice(offset, capture.Length));
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestOffset = offset;
            }

            coarseHits.Add((offset, ncc));
        }

        // Refine around every coarse local max above 0.5 * best (or top few).
        coarseHits.Sort((a, b) => b.Ncc.CompareTo(a.Ncc));
        var refineCenters = new HashSet<int> { bestOffset };
        foreach (var hit in coarseHits.Take(8))
        {
            refineCenters.Add(hit.Offset);
        }

        foreach (var center in refineCenters)
        {
            var fineStart = Math.Max(0, center - coarseStep * 2);
            fineStart -= fineStart % channels;
            var fineEnd = Math.Min(limit, center + coarseStep * 2);
            for (var offset = fineStart; offset <= fineEnd; offset += channels)
            {
                var ncc = Ncc(capture, searchRegion.Slice(offset, capture.Length));
                if (ncc > bestNcc)
                {
                    bestNcc = ncc;
                    bestOffset = offset;
                }
            }
        }

        // Competing peaks + final promotion of any denser hit that beats bestNcc.
        var minSep = sampleRate * channels / 20; // 50 ms
        minSep -= minSep % channels;
        minSep = Math.Max(channels, minSep);
        var peakCandidates = new List<CompetingPeak>();
        for (var offset = 0; offset <= limit; offset += channels)
        {
            // Dense near current best (±3 s); coarser elsewhere.
            if (Math.Abs(offset - bestOffset) > sampleRate * channels * 3
                && offset % (channels * 16) != 0)
            {
                continue;
            }

            var ncc = Ncc(capture, searchRegion.Slice(offset, capture.Length));
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestOffset = offset;
            }

            if (ncc < 0.5)
            {
                continue;
            }

            peakCandidates.Add(new CompetingPeak(
                offset,
                offset / (double)(sampleRate * channels) * 1000.0,
                ncc));
        }

        peakCandidates.Sort((a, b) => b.Ncc.CompareTo(a.Ncc));
        var competing = new List<CompetingPeak>();
        foreach (var p in peakCandidates)
        {
            if (competing.Any(c => Math.Abs(c.OffsetSamples - p.OffsetSamples) < minSep))
            {
                continue;
            }

            competing.Add(p);
            if (competing.Count >= maxCompetingPeaks)
            {
                break;
            }
        }

        var matchedRef = searchRegion.Slice(bestOffset, capture.Length);
        var fullDeltas = CompareAligned(capture, matchedRef, identityTolerance);
        var refAmp = Analyze(matchedRef);

        var matchedSourceMs =
            searchRegionStart.TotalMilliseconds
            + bestOffset / (double)(sampleRate * channels) * 1000.0;
        var armedDeltaMs = matchedSourceMs - armedAt.TotalMilliseconds;

        // Channel-swap probe (stereo interleaved).
        var channelSwapLikely = false;
        float channelSwapMaxDelta = float.MaxValue;
        if (channels == 2 && capture.Length >= 4)
        {
            var swapped = new float[matchedRef.Length];
            for (var i = 0; i + 1 < matchedRef.Length; i += 2)
            {
                swapped[i] = matchedRef[i + 1];
                swapped[i + 1] = matchedRef[i];
            }

            var swapDeltas = CompareAligned(capture, swapped, identityTolerance);
            channelSwapMaxDelta = swapDeltas.MaxAbsDelta;
            channelSwapLikely = swapDeltas.MaxAbsDelta < fullDeltas.MaxAbsDelta * 0.5f
                && swapDeltas.MaxAbsDelta <= identityTolerance * 8;
        }

        // Amplitude scale probe.
        double num = 0, den = 0;
        for (var i = 0; i < capture.Length; i++)
        {
            num += capture[i] * matchedRef[i];
            den += matchedRef[i] * matchedRef[i];
        }

        var gain = den > 1e-20 ? num / den : 1.0;
        float gainMaxDelta = 0;
        double gainSumSq = 0;
        for (var i = 0; i < capture.Length; i++)
        {
            var d = Math.Abs(capture[i] - (float)(gain * matchedRef[i]));
            if (d > gainMaxDelta)
            {
                gainMaxDelta = d;
            }

            gainSumSq += d * d;
        }

        var gainRms = (float)Math.Sqrt(gainSumSq / capture.Length);
        var amplitudeLikely = Math.Abs(gain - 1.0) > 0.02
            && gainMaxDelta <= Math.Max(identityTolerance * 4, fullDeltas.MaxAbsDelta * 0.25f);

        // Nearby lag residual (possible dup/omit of a few frames).
        var nearbyBetter = false;
        float nearbyMax = fullDeltas.MaxAbsDelta;
        for (var frameDelta = -8; frameDelta <= 8; frameDelta++)
        {
            if (frameDelta == 0)
            {
                continue;
            }

            var off = bestOffset + frameDelta * channels;
            if (off < 0 || off > limit)
            {
                continue;
            }

            var d = CompareAligned(capture, searchRegion.Slice(off, capture.Length), identityTolerance);
            if (d.MaxAbsDelta < nearbyMax * 0.5f)
            {
                nearbyBetter = true;
                nearbyMax = d.MaxAbsDelta;
            }
        }

        DivergenceKind kind;
        string? reason = null;
        var identity = bestNcc >= 0.999
            && fullDeltas.MaxAbsDelta <= identityTolerance
            && fullDeltas.FirstDivergeIndex < 0;

        if (capAmp.Rms < 1e-4f)
        {
            kind = DivergenceKind.SilenceUnsuitable;
            reason = "Silence floor.";
        }
        else if (bestNcc < MinNccForAlignment)
        {
            kind = DivergenceKind.InconclusiveAlignment;
            reason = $"Best NCC {bestNcc:F4} below alignment floor — not confirmed corruption.";
        }
        else if (Math.Abs(armedDeltaMs) > 2500)
        {
            kind = DivergenceKind.InconclusiveContent;
            reason =
                $"Best match at {matchedSourceMs:F1}ms is far from ArmedAt {armedAt.TotalMilliseconds:F1}ms "
                + $"(Δ {armedDeltaMs:F1}ms) — content correspondence weak.";
        }
        else if (identity)
        {
            kind = Math.Abs(armedDeltaMs) > 5
                ? DivergenceKind.PureTimeOffset
                : DivergenceKind.Identity;
            reason = kind == DivergenceKind.PureTimeOffset
                ? $"Sample-identical after lag; ArmedAt vs matched source Δ={armedDeltaMs:F1}ms (capture timing skew)."
                : "Sample identity after integer-sample alignment.";
        }
        else if (channelSwapLikely)
        {
            kind = DivergenceKind.ChannelSwapOrMismatch;
            reason = $"Channel-swapped residual maxΔ={channelSwapMaxDelta:E3} << aligned maxΔ={fullDeltas.MaxAbsDelta:E3}.";
        }
        else if (amplitudeLikely)
        {
            kind = DivergenceKind.AmplitudeScale;
            reason = $"Gain≈{gain:F4} reduces maxΔ to {gainMaxDelta:E3} (rmsΔ={gainRms:E3}).";
        }
        else if (nearbyBetter && nearbyMax <= identityTolerance * 8)
        {
            kind = DivergenceKind.PossibleFrameDupOrOmit;
            reason = $"Neighbor frame lag improves residual to maxΔ={nearbyMax:E3}.";
        }
        else if (bestNcc >= 0.99 && fullDeltas.MaxAbsDelta > identityTolerance)
        {
            kind = DivergenceKind.SampleValueCorruption;
            reason =
                $"High NCC ({bestNcc:F4}) with residual maxΔ={fullDeltas.MaxAbsDelta:E3} after sample-exact lag "
                + $"(firstDiv={fullDeltas.FirstDivergeIndex}).";
        }
        else if (bestNcc >= MinNccForAlignment && fullDeltas.MaxAbsDelta > identityTolerance)
        {
            // Moderate NCC + residual: do NOT auto-label corruption (user refinement).
            kind = DivergenceKind.InconclusiveAlignment;
            reason =
                $"NCC={bestNcc:F4} with maxΔ={fullDeltas.MaxAbsDelta:E3} after best lag — "
                + "not confirmed sample-exact corruption; competing peaks may indicate ambiguous align.";
        }
        else
        {
            kind = DivergenceKind.InconclusiveAlignment;
            reason = "Unable to classify.";
        }

        return new ExactAlignReport
        {
            Suitable = true,
            Kind = kind,
            Reason = reason,
            PeakNcc = bestNcc,
            BestLagSamples = bestOffset,
            BestLagMs = bestOffset / (double)(sampleRate * channels) * 1000.0,
            MatchedSourceMs = matchedSourceMs,
            ArmedAtMs = armedAt.TotalMilliseconds,
            RequestedStartMs = requestedStart.TotalMilliseconds,
            SearchRegionStartMs = searchRegionStart.TotalMilliseconds,
            ArmedDeltaMs = armedDeltaMs,
            MaxAbsDelta = fullDeltas.MaxAbsDelta,
            RmsDelta = fullDeltas.RmsDelta,
            FirstDivergeIndex = fullDeltas.FirstDivergeIndex,
            ComparedSamples = fullDeltas.ComparedSamples,
            CapturePeak = capAmp.Peak,
            CaptureRms = capAmp.Rms,
            ReferencePeak = refAmp.Peak,
            ReferenceRms = refAmp.Rms,
            EstimatedGain = gain,
            GainResidualMaxDelta = gainMaxDelta,
            ChannelSwapMaxDelta = channelSwapMaxDelta,
            CompetingPeaks = competing,
            SampleIdentity = identity,
        };
    }

    public sealed record ExactAlignReport
    {
        public bool Suitable { get; init; }
        public DivergenceKind Kind { get; init; }
        public string? Reason { get; init; }
        public double PeakNcc { get; init; }
        public int BestLagSamples { get; init; }
        public double BestLagMs { get; init; }
        public double MatchedSourceMs { get; init; }
        public double ArmedAtMs { get; init; }
        public double RequestedStartMs { get; init; }
        public double SearchRegionStartMs { get; init; }
        public double ArmedDeltaMs { get; init; }
        public float MaxAbsDelta { get; init; }
        public float RmsDelta { get; init; }
        public int FirstDivergeIndex { get; init; }
        public int ComparedSamples { get; init; }
        public float CapturePeak { get; init; }
        public float CaptureRms { get; init; }
        public float ReferencePeak { get; init; }
        public float ReferenceRms { get; init; }
        public double EstimatedGain { get; init; }
        public float GainResidualMaxDelta { get; init; }
        public float ChannelSwapMaxDelta { get; init; }
        public IReadOnlyList<CompetingPeak> CompetingPeaks { get; init; } = Array.Empty<CompetingPeak>();
        public bool SampleIdentity { get; init; }

        public override string ToString()
        {
            var peaks = string.Join(
                "; ",
                CompetingPeaks.Select(p => $"off={p.OffsetSamples}({p.OffsetMs:F1}ms)/ncc={p.Ncc:F4}"));
            return
                $"kind={Kind} identity={SampleIdentity} ncc={PeakNcc:F4} bestLag={BestLagSamples}({BestLagMs:F2}ms) "
                + $"matchedSrc={MatchedSourceMs:F1}ms armedAt={ArmedAtMs:F1}ms requested={RequestedStartMs:F1}ms "
                + $"armedΔ={ArmedDeltaMs:F1}ms maxΔ={MaxAbsDelta:E3} rmsΔ={RmsDelta:E3} firstDiv={FirstDivergeIndex} "
                + $"n={ComparedSamples} gain={EstimatedGain:F4} gainMaxΔ={GainResidualMaxDelta:E3} "
                + $"swapMaxΔ={ChannelSwapMaxDelta:E3} peaks=[{peaks}]"
                + (Reason is null ? "" : $" reason={Reason}");
        }
    }

    private static double Ncc(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
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

}
