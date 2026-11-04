using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Seeking;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public sealed class CrossfadePlaybackTests
{
    private static readonly AudioFormat Mono = AudioFormat.CreateFloat32(44100, 1);

    [Fact]
    public async Task ZeroDuration_MatchesHardCutGaplessSeam()
    {
        var aSamples = Enumerable.Repeat(0.25f, 400).ToArray();
        var bSamples = Enumerable.Repeat(0.75f, 400).ToArray();
        var expected = aSamples.Concat(bSamples).ToArray();

        var (captured, _) = await CapturePromoteAsync(
            aSamples,
            bSamples,
            crossfade: TimeSpan.Zero,
            curve: CrossfadeCurve.Linear,
            blockSamples: 64,
            minCaptureSamples: expected.Length);

        Assert.True(captured.Length >= expected.Length);
        Assert.Equal(expected, captured.AsSpan(0, expected.Length).ToArray());
    }

    [Fact]
    public async Task LinearCrossfade_ProducesExpectedOverlap()
    {
        const int fadeFrames = 100;
        var aSamples = Enumerable.Repeat(1f, 500).ToArray();
        var bSamples = Enumerable.Repeat(0f, 500).ToArray();

        var (captured, _) = await CapturePromoteAsync(
            aSamples,
            bSamples,
            crossfade: FramesToDuration(fadeFrames, Mono.SampleRate),
            curve: CrossfadeCurve.Linear,
            blockSamples: 64,
            minCaptureSamples: aSamples.Length + 50);

        var preFade = aSamples.Length - fadeFrames;
        for (var i = 0; i < preFade; i++)
        {
            Assert.Equal(1f, captured[i], precision: 3);
        }

        for (var i = 0; i < fadeFrames; i++)
        {
            var t = i / (float)fadeFrames;
            CrossfadeMixer.Gains(CrossfadeCurve.Linear, t, out var gA, out _);
            Assert.Equal(gA, captured[preFade + i], precision: 2);
        }

        Assert.Equal(0f, captured[preFade + fadeFrames], precision: 3);
    }

    [Fact]
    public async Task EqualPowerCrossfade_MidpointMatchesCurve()
    {
        const int fadeFrames = 200;
        var aSamples = Enumerable.Repeat(1f, 600).ToArray();
        var bSamples = Enumerable.Repeat(1f, 600).ToArray();

        var (captured, _) = await CapturePromoteAsync(
            aSamples,
            bSamples,
            crossfade: FramesToDuration(fadeFrames, Mono.SampleRate),
            curve: CrossfadeCurve.EqualPower,
            blockSamples: 50,
            minCaptureSamples: aSamples.Length + 50);

        var preFade = aSamples.Length - fadeFrames;
        var mid = preFade + (fadeFrames / 2);
        CrossfadeMixer.Gains(CrossfadeCurve.EqualPower, 0.5f, out var gA, out var gB);
        Assert.Equal(gA + gB, captured[mid], precision: 2);
    }

    [Fact]
    public async Task TrackTransitioned_FiresOnce_AtCrossfadeCommit()
    {
        var aSamples = Enumerable.Repeat(0.2f, 800).ToArray();
        var bSamples = Enumerable.Repeat(0.8f, 800).ToArray();

        var transitions = 0;
        var (captured, output) = await CapturePromoteAsync(
            aSamples,
            bSamples,
            crossfade: FramesToDuration(100, Mono.SampleRate),
            curve: CrossfadeCurve.Linear,
            blockSamples: 64,
            onTransitioned: () => Interlocked.Increment(ref transitions));

        Assert.Equal(1, transitions);
        Assert.Equal(1, output.InitializeCount);
        Assert.True(captured.Length > 0);
    }

    [Fact]
    public async Task UnderestimatedDuration_DoesNotStartBeforeProducerCompleted()
    {
        var aSamples = Enumerable.Repeat(0.4f, 800).ToArray();
        var bSamples = Enumerable.Repeat(0.6f, 400).ToArray();
        const int fadeFrames = 100;

        var (captured, _) = await CapturePromoteAsync(
            aSamples,
            bSamples,
            crossfade: FramesToDuration(fadeFrames, Mono.SampleRate),
            curve: CrossfadeCurve.Linear,
            blockSamples: 64,
            aDurationOverride: FramesToDuration(100, Mono.SampleRate),
            minCaptureSamples: aSamples.Length + 50);

        Assert.True(captured.Length >= aSamples.Length - fadeFrames);
        var preFade = aSamples.Length - fadeFrames;
        for (var i = 0; i < preFade; i++)
        {
            Assert.Equal(0.4f, captured[i], precision: 3);
        }
    }

    [Fact]
    public void CrossfadeDuration_RejectsNegativeAndOverCap()
    {
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory((_, _) =>
                Task.FromResult<IAudioDecoder>(FakeAudioDecoder.CreateSilence(Mono, TimeSpan.FromMilliseconds(10)))),
            new FakeAudioOutputFactory(() => new FakeAudioOutput()),
            new AudioEngineOptions());
        using var player = engine.CreatePlayer();

        Assert.Throws<ArgumentOutOfRangeException>(() => player.CrossfadeDuration = TimeSpan.FromMilliseconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => player.CrossfadeDuration = TimeSpan.FromSeconds(31));
        player.CrossfadeDuration = TimeSpan.FromSeconds(2);
        Assert.Equal(TimeSpan.FromSeconds(2), player.CrossfadeDuration);
        Assert.Equal(CrossfadeCurve.EqualPower, player.CrossfadeCurve);
    }

    private static TimeSpan FramesToDuration(int frames, int sampleRate) =>
        TimeSpan.FromSeconds(frames / (double)sampleRate);

    private static async Task<(float[] Captured, FakeAudioOutput Output)> CapturePromoteAsync(
        float[] aSamples,
        float[] bSamples,
        TimeSpan crossfade,
        CrossfadeCurve curve,
        int blockSamples,
        TimeSpan? aDurationOverride = null,
        Action? onTransitioned = null,
        int? minCaptureSamples = null)
    {
        FakeAudioOutput? output = null;
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            if (path == "a.wav")
            {
                IAudioDecoder decoder = new SequenceFakeDecoder(Mono, (float[])aSamples.Clone());
                if (aDurationOverride is { } d)
                {
                    decoder = new DurationOverrideDecoder(decoder, d);
                }

                return Task.FromResult(decoder);
            }

            return Task.FromResult<IAudioDecoder>(
                new SequenceFakeDecoder(Mono, (float[])bSamples.Clone()));
        });

        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: blockSamples, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                // Keep the ring large enough that an EOS-anchored fade finds a full tail.
                BufferDuration = TimeSpan.FromSeconds(1),
            });
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        player.CrossfadeDuration = crossfade;
        player.CrossfadeCurve = curve;

        var transitioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.TrackTransitioned += (_, _) =>
        {
            onTransitioned?.Invoke();
            transitioned.TrySetResult();
        };

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();
        await transitioned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("b.wav", player.SourcePath);

        var need = minCaptureSamples ?? (aSamples.Length + Math.Min(bSamples.Length, 200));
        var deadline = Environment.TickCount64 + 8000;
        while (output!.CapturedSamples.Count < need && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }

        player.Stop();
        return (output.CapturedSamples.ToArray(), output);
    }

    /// <summary>Wraps a decoder to lie about Duration while preserving real PCM length.</summary>
    private sealed class DurationOverrideDecoder : IAudioDecoder
    {
        private readonly IAudioDecoder _inner;

        public DurationOverrideDecoder(IAudioDecoder inner, TimeSpan duration)
        {
            _inner = inner;
            Duration = duration;
        }

        public AudioFormat Format => _inner.Format;
        public TimeSpan Duration { get; }
        public bool HasKnownDuration => true;
        public bool CanSeek => _inner.CanSeek;
        public int Read(Span<float> buffer) => _inner.Read(buffer);
        public AudioSeekResult Seek(TimeSpan position) => _inner.Seek(position);
        public void Dispose() => _inner.Dispose();
    }
}
