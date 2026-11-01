using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public sealed class AudioPlayerTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public async Task LoadPlayPauseStop_StateTransitions()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(300));
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();

        await player.LoadAsync("tone.wav");
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.True(player.HasSource);
        Assert.True(player.Duration > TimeSpan.Zero);

        player.Play();
        Assert.Equal(PlaybackState.Playing, player.State);

        player.Pause();
        Assert.Equal(PlaybackState.Paused, player.State);
        var pausedAt = player.Position;

        player.Play();
        Assert.Equal(PlaybackState.Playing, player.State);

        player.Stop();
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.True(pausedAt >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Stop_DoesNotRaisePlaybackCompleted()
    {
        // Hold decoder reads so playback cannot reach EOF before Stop (no wall-clock race).
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(hold),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("a.wav");
        player.Play();
        // Pump/output are alive; held decoder emits silence so EOF cannot arrive before Stop.
        Assert.Equal(PlaybackState.Playing, player.State);

        player.Stop();
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(0, Volatile.Read(ref completed));

        hold.Release();
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref completed));
    }

    [Fact]
    public async Task Stop_AfterNaturalCompletion_DoesNotRaiseAgain()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(80));
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("short.wav");
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref completed) == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, Volatile.Read(ref completed));
        player.Stop();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref completed));
    }

    [Fact]
    public async Task NaturalCompletion_RaisesPlaybackCompletedOnce()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(80));
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("short.wav");
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (completed == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, completed);
        Assert.Equal(PlaybackState.Stopped, player.State);

        await Task.Delay(100);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Volume_RejectsOutOfRange()
    {
        var decoder = FakeAudioDecoder.CreateSilence(Format, TimeSpan.FromMilliseconds(50));
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("x.wav");

        Assert.Throws<ArgumentOutOfRangeException>(() => player.Volume = -0.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Volume = 1.1f);
        player.Volume = 0.5f;
        Assert.Equal(0.5f, player.Volume);
    }

    [Fact]
    public async Task Seek_UpdatesPositions()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(1));
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("seek.wav");

        var target = TimeSpan.FromMilliseconds(250);
        player.Seek(target);

        Assert.InRange(player.Position.TotalMilliseconds, target.TotalMilliseconds - 30, target.TotalMilliseconds + 30);
        Assert.True(player.BufferedPosition >= player.Position - TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task Seek_BeyondDuration_Clamps()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(200));
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("seek.wav");

        player.Seek(TimeSpan.FromSeconds(10));
        Assert.InRange(player.Position.TotalMilliseconds, 180, 220);
    }

    [Fact]
    public async Task RepeatedPlayPauseStop_IsIdempotent()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(300));
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("r.wav");

        player.Play();
        player.Play();
        player.Pause();
        player.Pause();
        player.Stop();
        player.Stop();
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task Dispose_RejectsFurtherCommands()
    {
        var decoder = FakeAudioDecoder.CreateSilence(Format, TimeSpan.FromMilliseconds(50));
        using var engine = CreateEngine(decoder);
        var player = engine.CreatePlayer();
        await player.LoadAsync("d.wav");
        player.Dispose();

        Assert.Throws<ObjectDisposedException>(() => player.Play());
    }

    [Fact]
    public async Task ConcurrentLoads_LastWins()
    {
        var format = Format;
        var factory = new FakeAudioDecoderFactory(async (path, ct) =>
        {
            if (path.Contains("slow", StringComparison.Ordinal))
            {
                await Task.Delay(150, ct);
            }

            return FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(100));
        });
        var outputs = new FakeAudioOutputFactory();
        using var engine = new AudioEngine(factory, outputs);
        using var player = engine.CreatePlayer();

        var slow = player.LoadAsync("slow.wav");
        await Task.Delay(20);
        await player.LoadAsync("fast.wav");

        try
        {
            await slow;
        }
        catch (OperationCanceledException)
        {
            // may cancel depending on timing
        }

        Assert.Equal("fast.wav", factory.RequestedPaths[^1]);
        Assert.True(player.HasSource);
    }

    private static AudioEngine CreateEngine(FakeAudioDecoder decoder)
    {
        return new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            new FakeAudioOutputFactory());
    }
}
