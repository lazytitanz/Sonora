using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Deterministic teardown / recovery regressions. Uses holdable fakes and bounded waits — no hardware.
/// </summary>
public sealed class LifecycleTeardownHardeningTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentShutdownAndDispose_ShareSingleShutdownCompletion()
    {
        var output = new HoldableFakeAudioOutput("a");
        var source = new ForeverSource(Format);
        output.Initialize(source);
        output.Play();

        output.HoldShutdown();

        Task? disposeTask = null;
        Task? shutdownTask = null;
        try
        {
            disposeTask = RunOnDedicatedThread(output.Dispose, "Sonora.Test.ConcurrentDispose");
            Assert.True(
                output.WaitUntilShutdownHeld(TestBudget),
                $"Shutdown must be reached and held. ShutdownCount={output.ShutdownCount}, Idle={output.IsPullLoopIdle}");

            shutdownTask = RunOnDedicatedThread(output.Shutdown, "Sonora.Test.ConcurrentShutdown");
            Assert.False(disposeTask.IsCompleted, "Dispose must await shared Shutdown completion.");
            Assert.False(shutdownTask.IsCompleted, "Concurrent Shutdown must await the same completion.");

            output.ReleaseShutdown();
            await Task.WhenAll(disposeTask, shutdownTask).WaitAsync(TestBudget);

            Assert.Equal(1, output.ShutdownCount);
            Assert.True(output.IsShutdown);
            Assert.True(output.IsPullLoopIdle);
        }
        finally
        {
            output.ReleaseShutdown();
            output.ReleaseRead();
            if (disposeTask is { IsCompleted: false })
            {
                await disposeTask.WaitAsync(TestBudget);
            }

            if (shutdownTask is { IsCompleted: false })
            {
                try
                {
                    await shutdownTask.WaitAsync(TestBudget);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentPlayerDisposeAndDisposeAsync_ShareTeardownCompletion()
    {
        HoldableFakeAudioOutput? output = null;
        var outputFactory = new FakeAudioOutputFactory(_ =>
        {
            output = new HoldableFakeAudioOutput("a");
            return output;
        });

        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(3))),
            outputFactory,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        var player = engine.CreatePlayer();
        await player.LoadAsync("tone.wav");
        player.Play();
        Assert.NotNull(output);

        output!.HoldShutdown();
        Task? disposeTask = null;
        Task? disposeAsyncTask = null;
        try
        {
            disposeTask = RunOnDedicatedThread(player.Dispose, "Sonora.Test.PlayerDispose");
            Assert.True(
                output.WaitUntilShutdownHeld(TestBudget),
                $"Dispose must reach held Shutdown. ShutdownCount={output.ShutdownCount}, Idle={output.IsPullLoopIdle}");

            disposeAsyncTask = player.DisposeAsync().AsTask();
            Assert.False(
                disposeAsyncTask.IsCompleted,
                "DisposeAsync must await the shared Dispose teardown, not return early.");

            output.ReleaseShutdown();
            await Task.WhenAll(disposeTask, disposeAsyncTask).WaitAsync(TestBudget);

            Assert.Equal(1, output.ShutdownCount);
            Assert.True(output.IsShutdown);
            Assert.True(output.IsPullLoopIdle);
            Assert.Throws<ObjectDisposedException>(() => player.Play());
        }
        finally
        {
            output.ReleaseShutdown();
            if (disposeTask is { IsCompleted: false })
            {
                await disposeTask.WaitAsync(TestBudget);
            }

            if (disposeAsyncTask is { IsCompleted: false })
            {
                await disposeAsyncTask.WaitAsync(TestBudget);
            }
        }
    }

    [Fact]
    public async Task StaleStoppedFromPriorOutput_DoesNotDetachReplacement()
    {
        var first = new HoldableFakeAudioOutput("a");
        FakeAudioOutput? second = null;
        var created = new List<IAudioOutput>();
        var outputs = new FakeAudioOutputFactory(id =>
        {
            if (created.Count == 0)
            {
                created.Add(first);
                return first;
            }

            second = new FakeAudioOutput(endpointId: id ?? "b");
            created.Add(second);
            return second;
        });

        var devices = TwoDevices();
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        await player.SetOutputDeviceAsync("b").WaitAsync(TestBudget);
        Assert.Equal("b", player.ActiveOutputDeviceId);
        Assert.NotNull(second);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);

        // Deliver a Stopped that still names the prior output after B is current.
        player.OnOutputStopped(
            first,
            new AudioOutputStoppedEventArgs(
                reachedEndOfStream: false,
                exception: new AudioOutputException("stale-loss", AudioOutputFailureKind.DeviceUnavailable)));

        await Task.Delay(50);

        Assert.Equal("b", player.ActiveOutputDeviceId);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
        Assert.False(second!.IsShutdown);
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task DevicesChangedDuringUserSwitch_DoesNotStealSwitchOrLeaveSilentPlaying()
    {
        var holdable = new HoldableFakeAudioOutput("a");
        var created = new List<FakeAudioOutput>();
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
        player.Play();
        Assert.Equal(PlaybackState.Playing, player.State);

        holdable.HoldShutdown();
        var switching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.OutputDeviceChanged += (_, e) =>
        {
            if (e.Status == OutputDeviceStatus.Switching)
            {
                switching.TrySetResult();
            }
        };

        var switchTask = player.SetOutputDeviceAsync("b");
        await switching.Task.WaitAsync(TestBudget);
        Assert.True(holdable.WaitUntilShutdownHeld(TestBudget));
        Assert.Equal(OutputDeviceStatus.Switching, player.OutputDeviceStatus);

        // DevicesChanged during user switch must not cancel into silent Playing.
        devices.UpdateDevice(new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active));
        await Task.Delay(100);

        holdable.ReleaseShutdown();
        await switchTask.WaitAsync(TestBudget);

        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
        Assert.Equal("b", player.ActiveOutputDeviceId);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(2, created.Count);
        Assert.True(created[1].PlayCount >= 1, "Replacement output must have been started (not silent Playing).");
        Assert.True(holdable.IsShutdown);
    }

    [Fact]
    public async Task RecoveryWithNoActiveDevice_ShutsDownDetachedOutput()
    {
        HoldableFakeAudioOutput? output = null;
        var devices = new FakeAudioOutputDevices(
            new[] { new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active) },
            "a");
        var outputs = new FakeAudioOutputFactory(_ =>
        {
            output = new HoldableFakeAudioOutput("a");
            return output;
        });

        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();
        Assert.NotNull(output);

        // Remove all devices then raise DevicesChanged — recovery detaches and must shut down.
        devices.SetDevices(Array.Empty<AudioOutputDevice>(), defaultId: null);

        await WaitForAsync(
            () => output!.IsShutdown && player.OutputDeviceStatus == OutputDeviceStatus.Unavailable,
            TestBudget,
            () => $"Shutdown={output!.IsShutdown}, Status={player.OutputDeviceStatus}, Count={output.ShutdownCount}");

        Assert.Equal(1, output!.ShutdownCount);
        Assert.True(output.IsPullLoopIdle);
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task PauseDuringHeldRebind_EndsPaused()
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
        player.Play();

        holdable.HoldShutdown();
        var switching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.OutputDeviceChanged += (_, e) =>
        {
            if (e.Status == OutputDeviceStatus.Switching)
            {
                switching.TrySetResult();
            }
        };

        var switchTask = player.SetOutputDeviceAsync("b");
        await switching.Task.WaitAsync(TestBudget);
        Assert.True(holdable.WaitUntilShutdownHeld(TestBudget));

        player.Pause();
        Assert.Equal(PlaybackState.Paused, player.State);

        holdable.ReleaseShutdown();
        await switchTask.WaitAsync(TestBudget);

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
    }

    [Fact]
    public async Task DeviceSwitchThenExclusive_ShutsDownPriorOutputOnce()
    {
        var created = new List<FakeAudioOutput>();
        var outputs = new FakeAudioOutputFactory((req, _) =>
        {
            var o = new FakeAudioOutput(endpointId: req.DeviceId ?? "default");
            created.Add(o);
            return o;
        });

        var devices = TwoDevices();
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        await player.SetOutputDeviceAsync("b").WaitAsync(TestBudget);
        Assert.Equal("b", player.ActiveOutputDeviceId);

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.Shared,
        }).WaitAsync(TestBudget);

        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.True(created.Count >= 2);
        Assert.All(created.Take(created.Count - 1), o =>
        {
            Assert.True(o.IsShutdown, "Prior outputs must be shut down before exclusive attach.");
            Assert.Equal(1, o.ShutdownCount);
            Assert.True(o.IsPullLoopIdle);
        });
    }

    [Fact]
    public async Task EngineDispose_WithMultiplePlayers_DisposesAll()
    {
        var created = new List<HoldableFakeAudioOutput>();
        var outputs = new FakeAudioOutputFactory(_ =>
        {
            var o = new HoldableFakeAudioOutput("a");
            created.Add(o);
            return o;
        });

        var engine = new AudioEngine(
            new FakeAudioDecoderFactory(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(200))),
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero },
            TwoDevices());

        var players = new List<AudioPlayer>();
        for (var i = 0; i < 3; i++)
        {
            var p = engine.CreatePlayer();
            await p.LoadAsync($"t{i}.wav");
            p.Play();
            players.Add(p);
        }

        Assert.Equal(3, created.Count);
        created[1].ShutdownFault = () => new AudioOutputException("shutdown boom");

        await engine.DisposeAsync().AsTask().WaitAsync(TestBudget);

        Assert.All(created, o => Assert.True(o.ShutdownCount >= 1 || o.InitializeCount == 0));
        Assert.All(created, o => Assert.True(o.IsPullLoopIdle));
        foreach (var p in players)
        {
            Assert.Throws<ObjectDisposedException>(() => p.Play());
        }
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

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, Func<string>? diagnostics = null)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        var detail = diagnostics?.Invoke() ?? string.Empty;
        Assert.Fail($"Condition not met within {timeout}. {detail}");
    }

    private static Task RunOnDedicatedThread(Action action, string name)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return tcs.Task;
    }

    private sealed class ForeverSource : IPcmSampleSource
    {
        public ForeverSource(AudioFormat format) => Format = format;

        public AudioFormat Format { get; }
        public bool IsEndOfStream => false;

        public int Read(Span<float> buffer)
        {
            buffer.Clear();
            return buffer.Length;
        }

        public void ResetEndOfStream()
        {
        }
    }
}
