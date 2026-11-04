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
    public async Task Seek_SupersededByNewerSeek_DoesNotLeaveOutputPaused()
    {
        // Regression: Seek used to Pause the output for the whole Decoder.Seek window. A superseded
        // seek then abandoned without Play, leaving Playing + silent/frozen progress under rapid seeks.
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(3)));
        hold.Release();
        FakeAudioOutput? output = null;
        var factory = new FakeAudioOutputFactory(_ =>
        {
            output = new FakeAudioOutput();
            return output;
        });
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(hold),
            factory,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        Assert.NotNull(output);
        var playCountAtStart = output!.PlayCount;

        hold.HoldSeek();
        var first = Task.Run(() => player.Seek(TimeSpan.FromMilliseconds(100)));
        Assert.True(hold.WaitUntilSeekHeld(TimeSpan.FromSeconds(2)));

        // Newer seek supersedes; first must abort without leaving the transport paused.
        var second = Task.Run(() => player.Seek(TimeSpan.FromMilliseconds(200)));
        await Task.Delay(50);
        hold.ReleaseSeek();

        _ = await Record.ExceptionAsync(() => first);
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackState.Playing, player.State);
        // Seek no longer soft-Pauses the output for the MF window; the pull loop must stay alive.
        Assert.False(output.IsPullLoopIdle);
        Assert.True(output.PlayCount >= playCountAtStart);
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
        // Dedicated thread: under CI thread-pool pressure Task.Run can delay past a short WaitUntilSeekHeld.
        Exception? seekError = null;
        var seekThread = new Thread(() =>
        {
            try
            {
                player.Seek(TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                seekError = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Sonora.Test.SeekHeld",
        };
        seekThread.Start();
        try
        {
            Assert.True(
                hold.WaitUntilSeekHeld(TimeSpan.FromSeconds(10)),
                "Seek did not reach Decoder.Seek within the hold window.");

            // Pause must return without waiting for Decoder.Seek.
            Exception? pauseError = null;
            var pauseThread = new Thread(() =>
            {
                try
                {
                    player.Pause();
                }
                catch (Exception ex)
                {
                    pauseError = ex;
                }
            })
            {
                IsBackground = true,
                Name = "Sonora.Test.PauseDuringSeek",
            };
            pauseThread.Start();
            Assert.True(pauseThread.Join(TimeSpan.FromSeconds(2)), "Pause did not return while Decoder.Seek was held.");
            Assert.Null(pauseError);
            Assert.Equal(PlaybackState.Paused, player.State);

            // Stop's gate section must finish promptly; decoder rewind may still wait on HoldSeek.
            // Dedicated thread: under CI pool pressure Task.Run can delay past a short poll window.
            Exception? stopError = null;
            var stopThread = new Thread(() =>
            {
                try
                {
                    player.Stop();
                }
                catch (Exception ex)
                {
                    stopError = ex;
                }
            })
            {
                IsBackground = true,
                Name = "Sonora.Test.StopDuringSeek",
            };
            stopThread.Start();

            var stopDeadline = Environment.TickCount64 + 2000;
            while (player.State != PlaybackState.Stopped && Environment.TickCount64 < stopDeadline)
            {
                Thread.Sleep(5);
            }

            Assert.Equal(PlaybackState.Stopped, player.State);

            hold.ReleaseSeek();
            Assert.True(stopThread.Join(TimeSpan.FromSeconds(5)), "Stop thread did not finish after ReleaseSeek.");
            Assert.Null(stopError);
            Assert.True(seekThread.Join(TimeSpan.FromSeconds(5)), "Seek thread did not finish after ReleaseSeek.");
            // Seek may succeed or fault (superseded); it must not hang.
            _ = seekError;
        }
        finally
        {
            hold.ReleaseSeek();
            seekThread.Join(TimeSpan.FromSeconds(2));
        }
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
