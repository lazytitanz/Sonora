using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Position reporting during output rebind must freeze to the held audible snapshot — not the
/// sample-based estimate while a detached pull thread is still shutting down.
/// </summary>
public sealed class OutputSwitchPositionTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Position_DuringHeldShutdown_StaysAtSwitchSnapshot()
    {
        HoldableFakeAudioOutput? first = null;
        var create = 0;
        var factory = new FakeAudioOutputFactory((AudioOutputCreateRequest req, CancellationToken _) =>
        {
            create++;
            if (create == 1)
            {
                first = new HoldableFakeAudioOutput("a");
                return first;
            }

            return new FakeAudioOutput(endpointId: req.DeviceId ?? "b");
        });

        using var engine = CreateEngine(factory);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        Assert.NotNull(first);

        await Task.Delay(80);
        var before = player.Position;
        Assert.True(before > TimeSpan.Zero);

        first!.HoldShutdown();
        var switchTask = player.SetOutputDeviceAsync("b");
        Assert.True(first.WaitUntilShutdownHeld(TestBudget));
        Assert.Equal(OutputDeviceStatus.Switching, player.OutputDeviceStatus);

        // While the previous pull path is held in Shutdown, Position must not jump via sample estimate.
        var during = player.Position;
        Assert.Equal(before.TotalMilliseconds, during.TotalMilliseconds, tolerance: 50);

        first.ReleaseShutdown();
        await switchTask.WaitAsync(TestBudget);

        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
        Assert.Equal(during.TotalMilliseconds, player.Position.TotalMilliseconds, tolerance: 150);
    }

    [Fact]
    public async Task Position_AfterSuccessfulRebind_MatchesHeldWithoutJump()
    {
        var create = 0;
        var factory = new FakeAudioOutputFactory((AudioOutputCreateRequest req, CancellationToken _) =>
        {
            create++;
            return new FakeAudioOutput(endpointId: create == 1 ? "a" : (req.DeviceId ?? "b"));
        });

        using var engine = CreateEngine(factory);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        await Task.Delay(60);
        var before = player.Position;

        await player.SetOutputDeviceAsync("b");

        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(before.TotalMilliseconds, player.Position.TotalMilliseconds, tolerance: 150);
    }

    [Fact]
    public async Task Position_AfterFailedRebind_PreservesLastKnownHeld()
    {
        var create = 0;
        HoldableFakeAudioOutput? first = null;
        var factory = new FakeAudioOutputFactory((AudioOutputCreateRequest _, CancellationToken __) =>
        {
            create++;
            if (create == 1)
            {
                first = new HoldableFakeAudioOutput("a");
                return first;
            }

            throw new AudioOutputException("exclusive busy", AudioOutputFailureKind.DeviceBusy);
        });

        using var engine = CreateEngine(factory);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        await Task.Delay(60);
        var before = player.Position;
        Assert.NotNull(first);

        first!.HoldShutdown();
        var configureTask = player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });
        Assert.True(first.WaitUntilShutdownHeld(TestBudget));
        Assert.Equal(OutputDeviceStatus.Switching, player.OutputDeviceStatus);
        Assert.Equal(before.TotalMilliseconds, player.Position.TotalMilliseconds, tolerance: 50);

        first.ReleaseShutdown();
        var ex = await Assert.ThrowsAsync<AudioOutputException>(() => configureTask);
        Assert.Equal(AudioOutputFailureKind.DeviceBusy, ex.FailureKind);
        Assert.Equal(OutputDeviceStatus.Unavailable, player.OutputDeviceStatus);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(before.TotalMilliseconds, player.Position.TotalMilliseconds, tolerance: 50);
    }

    [Fact]
    public async Task Pause_DuringSwitching_PreservesHeldPosition()
    {
        HoldableFakeAudioOutput? first = null;
        var create = 0;
        var factory = new FakeAudioOutputFactory((AudioOutputCreateRequest _, CancellationToken __) =>
        {
            create++;
            if (create == 1)
            {
                first = new HoldableFakeAudioOutput("a");
                return first;
            }

            return new FakeAudioOutput(endpointId: "b");
        });

        using var engine = CreateEngine(factory);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        await Task.Delay(60);
        var before = player.Position;
        Assert.NotNull(first);

        first!.HoldShutdown();
        var switchTask = player.SetOutputDeviceAsync("b");
        Assert.True(first.WaitUntilShutdownHeld(TestBudget));

        player.Pause();
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(OutputDeviceStatus.Switching, player.OutputDeviceStatus);
        Assert.Equal(before.TotalMilliseconds, player.Position.TotalMilliseconds, tolerance: 50);

        first.ReleaseShutdown();
        await switchTask.WaitAsync(TestBudget);

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(before.TotalMilliseconds, player.Position.TotalMilliseconds, tolerance: 100);
    }

    [Fact]
    public async Task Seek_DuringSwitching_UpdatesFrozenPosition()
    {
        HoldableFakeAudioOutput? first = null;
        var create = 0;
        var factory = new FakeAudioOutputFactory((AudioOutputCreateRequest _, CancellationToken __) =>
        {
            create++;
            if (create == 1)
            {
                first = new HoldableFakeAudioOutput("a");
                return first;
            }

            return new FakeAudioOutput(endpointId: "b");
        });

        using var engine = CreateEngine(factory);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        await Task.Delay(40);
        Assert.NotNull(first);

        first!.HoldShutdown();
        var switchTask = player.SetOutputDeviceAsync("b");
        Assert.True(first.WaitUntilShutdownHeld(TestBudget));
        Assert.Equal(OutputDeviceStatus.Switching, player.OutputDeviceStatus);

        var target = TimeSpan.FromMilliseconds(500);
        var result = player.SeekWithResult(target);
        var expected = result.ActualPosition ?? result.RequestedPosition;
        Assert.Equal(OutputDeviceStatus.Switching, player.OutputDeviceStatus);
        Assert.Equal(expected, player.Position);

        first.ReleaseShutdown();
        await switchTask.WaitAsync(TestBudget);

        // Playing resumes from the seek landing; allow a small device-clock advance after attach.
        Assert.Equal(expected.TotalMilliseconds, player.Position.TotalMilliseconds, tolerance: 100);
    }

    [Fact]
    public async Task Seek_DuringSwitching_ThenFailedRebind_PreservesSeekPosition()
    {
        HoldableFakeAudioOutput? first = null;
        var create = 0;
        var factory = new FakeAudioOutputFactory((AudioOutputCreateRequest _, CancellationToken __) =>
        {
            create++;
            if (create == 1)
            {
                first = new HoldableFakeAudioOutput("a");
                return first;
            }

            throw new AudioOutputException("init failed", AudioOutputFailureKind.InitializationFailed);
        });

        using var engine = CreateEngine(factory);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();
        await Task.Delay(40);
        Assert.NotNull(first);

        first!.HoldShutdown();
        var configureTask = player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });
        Assert.True(first.WaitUntilShutdownHeld(TestBudget));

        var target = TimeSpan.FromMilliseconds(750);
        var result = player.SeekWithResult(target);
        var expected = result.ActualPosition ?? result.RequestedPosition;

        first.ReleaseShutdown();
        await Assert.ThrowsAsync<AudioOutputException>(() => configureTask);

        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(expected, player.Position);
    }

    private static FakeAudioOutputDevices TwoDevices() =>
        new(
            new[]
            {
                new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active),
                new AudioOutputDevice("b", "Headphones", false, AudioDeviceState.Active),
            },
            "a");

    private static AudioEngine CreateEngine(IAudioOutputFactory outputs) =>
        new(
            new FakeAudioDecoderFactory(
                new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(5)))),
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero, BufferDuration = TimeSpan.FromMilliseconds(200) },
            TwoDevices());
}
