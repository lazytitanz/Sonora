using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Deterministic theory of the production gapless pull path
/// (<see cref="TransitionSampleSource"/> + decode pumps + ring → fake pull output)
/// across a verified natural A→B promote while a single Read can span the seam.
/// Does not exercise WASAPI / MixFormat.
/// </summary>
public sealed class GaplessPromoteSeamProviderChainTests
{
    private static readonly AudioFormat Stereo = AudioFormat.CreateFloat32(44100, 2);

    /// <summary>
    /// Distinct L/R constants so a one-sample shift or channel swap is unambiguous.
    /// </summary>
    private const float AL = 0.25f;
    private const float AR = -0.75f;
    private const float BL = 0.80f;
    private const float BR = -0.40f;

    public static TheoryData<int, int, int> SeamCases => new()
    {
        // aFrames, bFrames, blockSamples (interleaved float count requested per pull)
        { 50, 80, 64 },   // common even pull; seam mid-stream
        { 50, 80, 36 },   // smaller even pull
        { 48, 64, 32 },   // A sample count divisible by block (frame-aligned boundary vs pull)
        { 49, 64, 32 },   // A sample count not divisible by block (seam inside a pull)
        { 37, 60, 64 },   // odd frame count (still even interleaved)
        { 100, 100, 40 }, // even spanning like mono Gapless_SpanningBuffer
        { 63, 90, 128 },  // large pull covering most of A
        { 16, 48, 12 },   // tiny even pulls, many seam-adjacent reads
    };

    [Theory]
    [MemberData(nameof(SeamCases))]
    public async Task StereoSeam_NaturalPromote_ConcatenatesExactly(
        int aFrames,
        int bFrames,
        int blockSamples)
    {
        Assert.True(blockSamples % Stereo.ChannelCount == 0,
            "Theory cases must use frame-aligned pull sizes; odd path is covered separately.");

        var aSamples = CreateStereoConstant(aFrames, AL, AR);
        var bSamples = CreateStereoConstant(bFrames, BL, BR);
        var expected = new float[aSamples.Length + bSamples.Length];
        aSamples.CopyTo(expected, 0);
        bSamples.CopyTo(expected, aSamples.Length);

        var result = await RunPromoteCaptureAsync(aSamples, bSamples, blockSamples);

        Assert.Equal(1, result.Transitions);
        Assert.Equal(1, result.InitializeCount);
        Assert.Equal("b.wav", result.SourcePathAfter);
        Assert.True(
            result.Captured.Length >= expected.Length,
            $"capture short: got {result.Captured.Length}, need ≥ {expected.Length}");

        // (1)(2)(3)(6) — exact A‖B interleaved floats, including L/R order at the seam.
        var prefix = result.Captured.AsSpan(0, expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(
                prefix[i] == expected[i],
                $"sample[{i}] expected={expected[i]:R} got={prefix[i]:R} "
                + $"(aSamples={aSamples.Length} block={blockSamples}; "
                + DescribeIndex(i, aSamples.Length) + ")");
        }

        // Trailing underrun/EOS padding must not invent non-zero content before expected length.
        Assert.All(prefix.ToArray(), s => Assert.True(s is AL or AR or BL or BR));

        // Frame integrity of the concatenated session prefix.
        Assert.Equal(0, expected.Length % Stereo.ChannelCount);
        for (var frame = 0; frame < aFrames; frame++)
        {
            Assert.Equal(AL, prefix[frame * 2]);
            Assert.Equal(AR, prefix[frame * 2 + 1]);
        }

        for (var frame = 0; frame < bFrames; frame++)
        {
            var i = aSamples.Length + frame * 2;
            Assert.Equal(BL, prefix[i]);
            Assert.Equal(BR, prefix[i + 1]);
        }

        // (5) Frame-aligned pull sizes: no odd request/return before the terminal EOS read.
        Assert.Empty(result.OddRequestLengths);
        Assert.Empty(result.OddReturnLengthsBeforeEos);

        // Every non-terminal return must be frame-aligned.
        Assert.All(
            result.ReturnLengths.Take(Math.Max(0, result.ReturnLengths.Length - 1)),
            n => Assert.Equal(0, n % Stereo.ChannelCount));
    }

    /// <summary>
    /// Documents the exact path that can request an odd interleaved stereo destination length:
    /// <c>FakeAudioOutput.Loop</c> → <c>IPcmSampleSource.Read(Span)</c> with
    /// <c>buffer.Length = blockSamples</c> when the test harness chooses an odd block size.
    /// Production WASAPI periods are framed by MixFormat block align; this harness path is not.
    /// The provider chain does not clamp destination length to a whole frame.
    /// </summary>
    [Fact]
    public async Task OddInterleavedRequest_Path_IsFakeOutputBlockSamples_NotFrameClamped()
    {
        const int aFrames = 40;
        const int bFrames = 40;
        const int oddBlock = 37; // odd interleaved float count

        var aSamples = CreateStereoConstant(aFrames, AL, AR);
        var bSamples = CreateStereoConstant(bFrames, BL, BR);
        var result = await RunPromoteCaptureAsync(aSamples, bSamples, oddBlock);

        Assert.Contains(oddBlock, result.RequestLengths);
        Assert.NotEmpty(result.OddRequestLengths);
        Assert.Contains(
            result.Notes,
            n => n.Contains("FakeAudioOutput", StringComparison.Ordinal));

        // With an odd destination, the chain may return an odd count (no frame clamp).
        var prefixLen = Math.Min(result.Captured.Length, aSamples.Length + bSamples.Length);
        var corrupted = !MatchesExpected(result.Captured.AsSpan(0, prefixLen), aSamples, bSamples);
        Assert.True(
            result.OddReturnLengthsBeforeEos.Length > 0 || corrupted,
            "Expected either odd returns or A‖B corruption when pulls request odd interleaved lengths.");
    }

    private static bool MatchesExpected(ReadOnlySpan<float> captured, float[] a, float[] b)
    {
        if (captured.Length < a.Length + b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (captured[i] != a[i])
            {
                return false;
            }
        }

        for (var i = 0; i < b.Length; i++)
        {
            if (captured[a.Length + i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeIndex(int interleavedIndex, int aSampleCount)
    {
        if (interleavedIndex < aSampleCount)
        {
            var frame = interleavedIndex / 2;
            var ch = interleavedIndex % 2 == 0 ? "L" : "R";
            return $"in A frame {frame} ch {ch}";
        }

        var bIndex = interleavedIndex - aSampleCount;
        var bFrame = bIndex / 2;
        var bCh = bIndex % 2 == 0 ? "L" : "R";
        return $"in B frame {bFrame} ch {bCh} (seam at sample {aSampleCount})";
    }

    private static float[] CreateStereoConstant(int frames, float left, float right)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[i * 2] = left;
            samples[i * 2 + 1] = right;
        }

        return samples;
    }

    private static async Task<SeamResult> RunPromoteCaptureAsync(
        float[] aSamples,
        float[] bSamples,
        int blockSamples)
    {
        RequestProbeFakeOutput? output = null;
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            var samples = path == "a.wav" ? aSamples : bSamples;
            return Task.FromResult<IAudioDecoder>(
                new SequenceFakeDecoder(Stereo, (float[])samples.Clone()));
        });
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new RequestProbeFakeOutput(blockSamples);
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                BufferDuration = TimeSpan.FromSeconds(1),
            });
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.Volume = 1f;

        var transitions = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref completed) == 0 && sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, Volatile.Read(ref completed));
        Assert.NotNull(output);

        float[] captured;
        lock (output!.CapturedSamples)
        {
            captured = output.CapturedSamples.ToArray();
        }

        var notes = new List<string>
        {
            "Odd interleaved Read requests originate at FakeAudioOutput.Loop → "
            + "IPcmSampleSource.Read(buffer) when blockSamples is odd; "
            + "TransitionSampleSource / PcmRingBuffer / DecodePump do not clamp to ChannelCount. "
            + "Production WasapiAudioOutput periods are sized in frames (MixFormat block align).",
        };

        return new SeamResult
        {
            Captured = captured,
            Transitions = Volatile.Read(ref transitions),
            InitializeCount = output.InitializeCount,
            SourcePathAfter = player.SourcePath,
            RequestLengths = output.RequestLengths.ToArray(),
            ReturnLengths = output.ReturnLengths.ToArray(),
            OddRequestLengths = output.RequestLengths.Where(n => n % Stereo.ChannelCount != 0).ToArray(),
            OddReturnLengthsBeforeEos = output.ReturnLengths
                .Take(Math.Max(0, output.ReturnLengths.Count - 1))
                .Where(n => n > 0 && n % Stereo.ChannelCount != 0)
                .ToArray(),
            Notes = notes,
        };
    }

    private sealed class SeamResult
    {
        public required float[] Captured { get; init; }
        public required int Transitions { get; init; }
        public required int InitializeCount { get; init; }
        public required string? SourcePathAfter { get; init; }
        public required int[] RequestLengths { get; init; }
        public required int[] ReturnLengths { get; init; }
        public required int[] OddRequestLengths { get; init; }
        public required int[] OddReturnLengthsBeforeEos { get; init; }
        public required List<string> Notes { get; init; }
    }

    /// <summary>
    /// Records every pull <c>Read</c> request/return length through a probing wrapper.
    /// </summary>
    private sealed class RequestProbeFakeOutput : FakeAudioOutput
    {
        public List<int> RequestLengths { get; } = new();
        public List<int> ReturnLengths { get; } = new();

        public RequestProbeFakeOutput(int blockSamples)
            : base(blockSamples, latency: TimeSpan.FromMilliseconds(1))
        {
        }

        public override void Initialize(IPcmSampleSource source)
        {
            base.Initialize(new ProbeSource(source, RequestLengths, ReturnLengths));
        }
    }

    private sealed class ProbeSource : IPcmSampleSource
    {
        private readonly IPcmSampleSource _inner;
        private readonly List<int> _requests;
        private readonly List<int> _returns;

        public ProbeSource(IPcmSampleSource inner, List<int> requests, List<int> returns)
        {
            _inner = inner;
            _requests = requests;
            _returns = returns;
        }

        public AudioFormat Format => _inner.Format;
        public bool IsEndOfStream => _inner.IsEndOfStream;

        public int Read(Span<float> destination)
        {
            _requests.Add(destination.Length);
            var n = _inner.Read(destination);
            _returns.Add(n);
            return n;
        }
    }
}
