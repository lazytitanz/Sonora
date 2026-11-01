using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public sealed class GaplessPlaybackTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 1);

    [Fact]
    public async Task Preload_DoesNotInterruptPlaybackOrChangePosition()
    {
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            if (path == "a.wav")
            {
                return Task.FromResult<IAudioDecoder>(hold);
            }

            return Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(200)));
        });
        var outputs = new FakeAudioOutputFactory();
        using var engine = new AudioEngine(factory, outputs, new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();

        await player.LoadAsync("a.wav");
        player.Play();
        var before = player.Position;
        Assert.Equal(PlaybackState.Playing, player.State);

        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        Assert.Equal("b.wav", player.NextTrackPath);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal("a.wav", player.SourcePath);
        Assert.True(player.Position >= before);

        hold.Release();
        player.Stop();
    }

    [Fact]
    public async Task Preload_Replace_UsesNewestRequest()
    {
        var factory = new FakeAudioDecoderFactory((path, _) =>
            Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(150))));
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        await player.PreloadNextAsync("c.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        Assert.Equal("c.wav", player.NextTrackPath);
    }

    [Fact]
    public async Task Preload_InvalidFile_DoesNotStopCurrent()
    {
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            if (path == "bad.wav")
            {
                throw new AudioException("boom");
            }

            return Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(300)));
        });
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();

        await player.PreloadNextAsync("bad.wav");
        Assert.Equal(NextTrackState.Failed, player.NextTrackState);
        Assert.Equal(PlaybackState.Playing, player.State);
        player.Stop();
    }

    [Fact]
    public async Task ClearNext_ReleasesReadySlot()
    {
        var factory = new FakeAudioDecoderFactory((path, _) =>
            Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(100))));
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        player.ClearNext();
        Assert.Equal(NextTrackState.None, player.NextTrackState);
        Assert.Null(player.NextTrackPath);
    }

    [Fact]
    public async Task Gapless_SpanningBuffer_NoSilenceDupOrDrop()
    {
        // Track A: 100 frames of 1.0; Track B: 100 frames of 2.0 — mono.
        var aSamples = Enumerable.Repeat(1f, 100).ToArray();
        var bSamples = Enumerable.Repeat(2f, 100).ToArray();
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            var samples = path == "a.wav" ? aSamples : bSamples;
            // New array each open so dispose/seek isolation is fine.
            return Task.FromResult<IAudioDecoder>(
                new SequenceFakeDecoder(Format, (float[])samples.Clone()));
        });

        // Odd block size so reads straddle the 100-sample boundary.
        FakeAudioOutput? output = null;
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 37, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero, BufferDuration = TimeSpan.FromSeconds(1) });
        using var player = engine.CreatePlayer();
        var transitions = 0;
        var transitionThread = -1;
        player.TrackTransitioned += (_, _) =>
        {
            Interlocked.Increment(ref transitions);
            transitionThread = Environment.CurrentManagedThreadId;
        };
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref completed) == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, Volatile.Read(ref completed));
        Assert.Equal(1, Volatile.Read(ref transitions));
        Assert.NotNull(output);
        Assert.Equal(1, output!.InitializeCount);
        Assert.NotEqual(output.PullThreadId, transitionThread);

        float[] captured;
        lock (output.CapturedSamples)
        {
            captured = output.CapturedSamples.ToArray();
        }

        // Contiguous 100×1 then 100×2 — allow trailing underrun zeros after session EOS.
        Assert.True(captured.Length >= 200);
        Assert.All(captured.Take(100), s => Assert.Equal(1f, s));
        Assert.All(captured.Skip(100).Take(100), s => Assert.Equal(2f, s));
        Assert.Equal("b.wav", player.SourcePath);
    }

    [Fact]
    public async Task Gapless_DoesNotRaisePlaybackCompletedBetweenTracks()
    {
        // Root cause of CI flake: both tracks were short finite tones. By the time the test
        // observed TrackTransitioned, B had often already reached EOS and raised the single
        // legitimate PlaybackCompleted — so "expected 0 completed" failed even though the
        // completion was not premature at the A→B boundary. Hold B open after promotion.
        HoldableFakeDecoder? trackB = null;
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            if (path == "a.wav")
            {
                return Task.FromResult<IAudioDecoder>(
                    FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(50)));
            }

            trackB = new HoldableFakeDecoder(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(50)));
            return Task.FromResult<IAudioDecoder>(trackB);
        });

        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        var completed = 0;
        var transitions = 0;
        var eventOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        player.TrackTransitioned += (_, _) =>
        {
            Interlocked.Increment(ref transitions);
            eventOrder.Enqueue(
                $"transition source={player.SourcePath} next={player.NextTrackState} completed={Volatile.Read(ref completed)}");
        };
        player.PlaybackCompleted += (_, _) =>
        {
            Interlocked.Increment(ref completed);
            eventOrder.Enqueue(
                $"complete source={player.SourcePath} next={player.NextTrackState}");
        };

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.NotNull(trackB);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();

        await WaitForAsync(() => Volatile.Read(ref transitions) >= 1, TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref transitions));
        Assert.Equal("b.wav", player.SourcePath);
        Assert.Equal(NextTrackState.None, player.NextTrackState);
        Assert.True(
            Volatile.Read(ref completed) == 0,
            "PlaybackCompleted must not fire at A→B while B is still held open. Order: "
            + string.Join(" | ", eventOrder));

        trackB.Release();
        await WaitForAsync(() => Volatile.Read(ref completed) >= 1, TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref completed));
        Assert.Equal(1, Volatile.Read(ref transitions));
        Assert.Equal(
            new[] { "transition", "complete" },
            eventOrder.Select(e => e.Split(' ')[0]).ToArray());
    }

    [Fact]
    public async Task MissingNext_RaisesPlaybackCompletedOnce()
    {
        var factory = new FakeAudioDecoderFactory((path, _) =>
            Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(60))));
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("a.wav");
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref completed) == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, Volatile.Read(ref completed));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref completed));
    }

    [Fact]
    public async Task Seek_PreservesReadyNext_WithoutTransition()
    {
        var factory = new FakeAudioDecoderFactory((path, _) =>
            Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(500))));
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        var transitions = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        player.Play();
        player.Seek(TimeSpan.FromMilliseconds(50));

        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        Assert.Equal("b.wav", player.NextTrackPath);
        Assert.Equal("a.wav", player.SourcePath);
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref transitions));
        player.Stop();
        Assert.Equal(NextTrackState.None, player.NextTrackState);
    }

    [Fact]
    public async Task LoadAsync_ClearsNext()
    {
        var factory = new FakeAudioDecoderFactory((path, _) =>
            Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(100))));
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        await player.LoadAsync("c.wav");
        Assert.Equal(NextTrackState.None, player.NextTrackState);
        Assert.Equal("c.wav", player.SourcePath);
    }

    [Fact]
    public async Task Effects_PersistAcrossGaplessTransition()
    {
        var factory = new FakeAudioDecoderFactory((path, _) =>
            Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(80))));
        using var engine = new AudioEngine(
            factory,
            new FakeAudioOutputFactory(),
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                EffectFactory = new FakeAudioEffectFactory(),
            });
        using var player = engine.CreatePlayer();
        var eq = player.Effects.AddEqualizer();
        eq.SetBandGain(0, 4f);
        var transitions = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref transitions) == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, Volatile.Read(ref transitions));
        Assert.Equal(4f, eq.GetBandGain(0));
        Assert.Same(eq, player.Effects.Items[0]);
    }

    [Fact]
    public async Task Stop_DuringGapless_NeverRaisesPlaybackCompleted()
    {
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            if (path == "a.wav")
            {
                return Task.FromResult<IAudioDecoder>(hold);
            }

            return Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(200)));
        });
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        player.Play();
        player.Stop();
        hold.Release();
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.Equal(NextTrackState.None, player.NextTrackState);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition not met before timeout.");
    }
}
