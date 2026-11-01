using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public class OutputSwitchHardeningTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 1);

    [Fact]
    public async Task HoldablePullLoop_SwitchWaitsForQuiescence_NoDualConsumer()
    {
        var holdable = new HoldableFakeAudioOutput("a");
        var created = new List<IAudioOutput>();
        var outputs = new FakeAudioOutputFactory(id =>
        {
            if (created.Count == 0)
            {
                created.Add(holdable);
                return holdable;
            }

            var next = new FakeAudioOutput(endpointId: id ?? "b");
            created.Add(next);
            return next;
        });

        var devices = TwoDevices();
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        holdable.HoldRead();
        holdable.HoldShutdown();
        player.Play();
        Assert.True(holdable.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        var switching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.OutputDeviceChanged += (_, e) =>
        {
            if (e.Status == OutputDeviceStatus.Switching)
            {
                switching.TrySetResult();
            }
        };

        var switchTask = player.SetOutputDeviceAsync("b");
        await switching.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(holdable.WaitUntilShutdownHeld(TimeSpan.FromSeconds(2)));
        Assert.False(switchTask.IsCompleted);
        Assert.Single(created);
        Assert.Equal(OutputDeviceStatus.Switching, player.OutputDeviceStatus);

        holdable.ReleaseShutdown();
        await switchTask;

        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
        Assert.Equal("b", player.ActiveOutputDeviceId);
        Assert.True(holdable.IsShutdown);
        Assert.Equal(2, created.Count);
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task StopDuringHeldTeardown_SupersedesSwitch()
    {
        var holdable = new HoldableFakeAudioOutput("a");
        var count = 0;
        var outputs = new FakeAudioOutputFactory(id =>
        {
            count++;
            return count == 1 ? holdable : new FakeAudioOutput(endpointId: id ?? "b");
        });

        var devices = TwoDevices();
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        holdable.HoldRead();
        holdable.HoldShutdown();
        player.Play();
        Assert.True(holdable.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        var switching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.OutputDeviceChanged += (_, e) =>
        {
            if (e.Status == OutputDeviceStatus.Switching)
            {
                switching.TrySetResult();
            }
        };

        var switchTask = player.SetOutputDeviceAsync("b");
        await switching.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(holdable.WaitUntilShutdownHeld(TimeSpan.FromSeconds(2)));

        player.Stop();
        holdable.ReleaseShutdown();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switchTask);
        Assert.Equal(OutputDeviceStatus.Unavailable, player.OutputDeviceStatus);
        Assert.Equal(OutputRecoveryIntent.None, player.OutputRecoveryIntent);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(1, count); // replacement never created
    }

    [Fact]
    public async Task DisposeDuringHeldTeardown_SupersedesSwitch()
    {
        var holdable = new HoldableFakeAudioOutput("a");
        var count = 0;
        var outputs = new FakeAudioOutputFactory(id =>
        {
            count++;
            return count == 1 ? holdable : new FakeAudioOutput(endpointId: id ?? "b");
        });

        var devices = TwoDevices();
        var engine = CreateEngine(devices, outputs);
        var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        holdable.HoldRead();
        holdable.HoldShutdown();
        player.Play();
        Assert.True(holdable.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        var switching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.OutputDeviceChanged += (_, e) =>
        {
            if (e.Status == OutputDeviceStatus.Switching)
            {
                switching.TrySetResult();
            }
        };

        var switchTask = player.SetOutputDeviceAsync("b");
        await switching.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(holdable.WaitUntilShutdownHeld(TimeSpan.FromSeconds(2)));

        player.Dispose();
        holdable.ReleaseShutdown();

        var ex = await Record.ExceptionAsync(() => switchTask);
        Assert.NotNull(ex);
        Assert.True(ex is OperationCanceledException or ObjectDisposedException);
        engine.Dispose();
    }

    [Fact]
    public async Task OverlappingSwitches_FirstSuperseded_SecondAttaches()
    {
        HoldableFakeAudioOutput? first = null;
        var createBStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var outputs = new FakeAudioOutputFactory((id, ct) =>
        {
            count++;
            if (count == 1)
            {
                first = new HoldableFakeAudioOutput("a");
                return first;
            }

            if (id == "b")
            {
                createBStarted.TrySetResult();
                while (!ct.IsCancellationRequested)
                {
                    Thread.Sleep(10);
                }

                ct.ThrowIfCancellationRequested();
            }

            return new FakeAudioOutput(endpointId: id ?? "default");
        });

        var devices = TwoDevices();
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        first!.HoldRead();
        first.HoldShutdown();
        player.Play();
        Assert.True(first.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        var switching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.OutputDeviceChanged += (_, e) =>
        {
            if (e.Status == OutputDeviceStatus.Switching)
            {
                switching.TrySetResult();
            }
        };

        var firstSwitch = player.SetOutputDeviceAsync("b");
        await switching.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first.WaitUntilShutdownHeld(TimeSpan.FromSeconds(2)));

        // Unblock first teardown so it can reach Create("b") and then be superseded.
        first.ReleaseShutdown();
        await createBStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var secondSwitch = player.SetOutputDeviceAsync("a");

        var firstError = await Record.ExceptionAsync(() => firstSwitch);
        Assert.IsType<OperationCanceledException>(firstError);
        await secondSwitch;

        Assert.Equal("a", player.PreferredOutputDeviceId);
        Assert.Equal("a", player.ActiveOutputDeviceId);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
    }

    [Fact]
    public async Task TeardownFailure_DoesNotAttachReplacement()
    {
        var holdable = new HoldableFakeAudioOutput("a");
        holdable.ShutdownFault = () => new AudioOutputException("simulated shutdown failure");

        var createCount = 0;
        var outputs = new FakeAudioOutputFactory(id =>
        {
            createCount++;
            return createCount == 1
                ? holdable
                : new FakeAudioOutput(endpointId: id ?? "b");
        });

        var devices = TwoDevices();
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        // Not playing — Shutdown joins immediately, then ShutdownFault throws.
        var errors = 0;
        player.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        await Assert.ThrowsAsync<AudioOutputException>(() => player.SetOutputDeviceAsync("b"));

        Assert.Equal(1, createCount); // no replacement created
        Assert.Equal(OutputDeviceStatus.Unavailable, player.OutputDeviceStatus);
        Assert.Null(player.ActiveOutputDeviceId);
        Assert.True(errors >= 1);
    }

    [Fact]
    public async Task TeardownFailure_WithLiveConsumer_DoesNotAttachReplacement()
    {
        var holdable = new HoldableFakeAudioOutput("a", shutdownTimeout: TimeSpan.FromMilliseconds(200))
        {
            // Keep the pull loop stuck through Shutdown so Join cannot guarantee quiescence.
            ReleaseReadOnShutdown = false,
        };
        var createCount = 0;
        var outputs = new FakeAudioOutputFactory(id =>
        {
            createCount++;
            return createCount == 1
                ? holdable
                : new FakeAudioOutput(endpointId: id ?? "b");
        });

        var devices = TwoDevices();
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        holdable.HoldRead();
        player.Play();
        Assert.True(holdable.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        var errors = 0;
        player.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        var ex = await Assert.ThrowsAsync<AudioOutputException>(() => player.SetOutputDeviceAsync("b"));
        Assert.Contains("Timed out", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, createCount);
        Assert.Equal(OutputDeviceStatus.Unavailable, player.OutputDeviceStatus);
        Assert.Null(player.ActiveOutputDeviceId);
        Assert.True(errors >= 1);

        holdable.ReleaseRead();
    }

    [Fact]
    public async Task RapidSelection_SupersededThrows_DoesNotReportSuccess()
    {
        var devices = TwoDevices();
        var createBStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputs = new FakeAudioOutputFactory((id, ct) =>
        {
            if (id == "b")
            {
                createBStarted.TrySetResult();
                while (!ct.IsCancellationRequested)
                {
                    Thread.Sleep(10);
                }

                ct.ThrowIfCancellationRequested();
            }

            return new FakeAudioOutput(endpointId: id ?? "default");
        });

        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        var first = player.SetOutputDeviceAsync("b");
        await createBStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = player.SetOutputDeviceAsync("a");

        await second;
        var firstError = await Record.ExceptionAsync(() => first);
        Assert.IsType<OperationCanceledException>(firstError);

        Assert.Equal("a", player.PreferredOutputDeviceId);
        Assert.Equal("a", player.ActiveOutputDeviceId);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
    }

    [Fact]
    public async Task SwitchingStatus_VisibleWhileDetached()
    {
        var holdable = new HoldableFakeAudioOutput("a");
        var count = 0;
        var outputs = new FakeAudioOutputFactory(id =>
        {
            count++;
            return count == 1 ? holdable : new FakeAudioOutput(endpointId: id ?? "b");
        });

        var devices = TwoDevices();
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        holdable.HoldRead();
        holdable.HoldShutdown();
        player.Play();
        Assert.True(holdable.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        var switchingArgs = new TaskCompletionSource<OutputDeviceChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        player.OutputDeviceChanged += (_, e) =>
        {
            if (e.Reason == OutputDeviceChangeReason.Switching)
            {
                switchingArgs.TrySetResult(e);
            }
        };

        var switchTask = player.SetOutputDeviceAsync("b");
        var args = await switchingArgs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(holdable.WaitUntilShutdownHeld(TimeSpan.FromSeconds(2)));
        Assert.Equal(OutputDeviceStatus.Switching, args.Status);
        Assert.Null(args.ActiveDeviceId);
        Assert.Equal(OutputDeviceStatus.Switching, player.OutputDeviceStatus);
        Assert.False(switchTask.IsCompleted);

        holdable.ReleaseShutdown();
        await switchTask;
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
    }

    private static FakeAudioOutputDevices TwoDevices() =>
        new(
            new[]
            {
                new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active),
                new AudioOutputDevice("b", "Headphones", false, AudioDeviceState.Active),
            },
            "a");

    private static AudioEngine CreateEngine(FakeAudioOutputDevices devices, FakeAudioOutputFactory outputs)
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2));
        return new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero },
            devices);
    }
}
