using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>Overlapping command sequences with holdable fakes (deterministic barriers).</summary>
public sealed class ConcurrentLifecycleTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public async Task Load_ThenImmediateStop_LeavesStoppedWithoutCompletion()
    {
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
        player.Stop();

        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(0, Volatile.Read(ref completed));
    }

    [Fact]
    public async Task Seek_WhileOutputReadHeld_ThenStop_SingleConsumer()
    {
        HoldableFakeAudioOutput? output = null;
        var factory = new FakeAudioOutputFactory(_ =>
        {
            output = new HoldableFakeAudioOutput("a");
            return output;
        });

        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(
                new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(3)))),
            factory,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        Assert.NotNull(output);

        output!.HoldRead();
        Assert.True(output.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        // Seek pauses/joins under player logic; release read so seek can proceed.
        var seekTask = Task.Run(() => player.Seek(TimeSpan.FromMilliseconds(100)));
        output.ReleaseRead();
        await seekTask.WaitAsync(TimeSpan.FromSeconds(5));

        player.Stop();
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.True(output.IsPullLoopIdle || output.StopCount > 0);
    }

    [Fact]
    public async Task Seek_WhileDecoderSeekHeld_DoesNotBlockPauseOrStop()
    {
        // Regression: Seek used to hold the command gate across Decoder.Seek. A slow MF seek
        // froze Pause/Stop/Dispose (TestPlayer AppHang). Commands must complete while Seek is mid-decoder.
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(3)));
        hold.Release();
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(hold),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();

        hold.HoldSeek();
        var seekTask = Task.Run(() => player.Seek(TimeSpan.FromMilliseconds(200)));
        Assert.True(hold.WaitUntilSeekHeld(TimeSpan.FromSeconds(2)));

        // Pause must return without waiting for Decoder.Seek.
        var paused = Task.Run(player.Pause);
        await paused.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PlaybackState.Paused, player.State);

        // Stop's gate section must finish; decoder rewind may still be waiting on HoldSeek.
        var stopTask = Task.Run(player.Stop);
        await Task.Delay(100);
        Assert.Equal(PlaybackState.Stopped, player.State);

        hold.ReleaseSeek();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        // Seek may succeed or fault (superseded); it must not hang.
        var finished = await Task.WhenAny(seekTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(seekTask, finished);
        _ = await Record.ExceptionAsync(() => seekTask);
    }

    [Fact]
    public async Task Seek_ThenLoad_SupersedesSession()
    {
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory((_, _) =>
                Task.FromResult<IAudioDecoder>(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)))),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        player.Seek(TimeSpan.FromMilliseconds(50));
        await player.LoadAsync("b.wav");
        Assert.Equal("b.wav", player.SourcePath);
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task Preload_ClearNext_DoesNotRaiseCompletion()
    {
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory((_, _) =>
                Task.FromResult<IAudioDecoder>(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(1)))),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero, BufferDuration = TimeSpan.FromMilliseconds(50) });
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.ClearNext();
        Assert.Equal(NextTrackState.None, player.NextTrackState);
        Assert.Equal(0, Volatile.Read(ref completed));
    }
}
