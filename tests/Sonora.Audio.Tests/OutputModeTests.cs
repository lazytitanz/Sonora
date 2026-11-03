using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public class OutputModeTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 1);

    [Fact]
    public async Task SharedMode_IsDefault()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        Assert.Equal(AudioOutputMode.Shared, player.OutputConfiguration.Mode);
        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
        Assert.Equal(ExclusiveFallbackReason.None, player.OutputFallbackReason);
    }

    [Fact]
    public async Task ConfigureExclusive_BeforeLoad_StoresPreferenceWithoutAttaching()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });

        Assert.False(player.HasSource);
        Assert.Equal(AudioOutputMode.Exclusive, player.OutputConfiguration.Mode);
        Assert.Null(player.EffectiveOutputMode);

        await player.LoadAsync("track.wav");
        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task ConfigureExclusive_AfterLoad_BeforePlay_AttachesExclusive()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });

        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.True(player.HasSource);
    }

    [Fact]
    public async Task ConfigureExclusive_SetsEffectiveExclusive()
    {
        var outputs = new FakeAudioOutputFactory((req, _) =>
            new FakeAudioOutput(endpointId: req.DeviceId ?? "a"));
        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });

        Assert.Equal(AudioOutputMode.Exclusive, player.OutputConfiguration.Mode);
        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.Contains(outputs.Created, c => c.Request.Mode == AudioOutputMode.Exclusive);
    }

    [Fact]
    public async Task StrictExclusive_CreateFailure_PreservesSourceForRetry()
    {
        // Instrumentation: exclusive create must run only after old-output Shutdown succeeds.
        // Prior CI flake: Pause left a ThreadPool pull loop runnable; under parallel load the
        // join timed out and ConfigureOutputAsync threw Unspecified *before* exclusive create.
        var exclusiveCreateCount = 0;
        var sharedCreateCount = 0;
        FakeAudioOutput? sharedOutput = null;
        var outputs = new FakeAudioOutputFactory((req, _) =>
        {
            if (req.Mode == AudioOutputMode.Exclusive)
            {
                Interlocked.Increment(ref exclusiveCreateCount);
                throw new AudioOutputException(
                    "busy",
                    AudioOutputFailureKind.DeviceBusy);
            }

            Interlocked.Increment(ref sharedCreateCount);
            sharedOutput = new FakeAudioOutput(endpointId: req.DeviceId ?? "a");
            return sharedOutput;
        });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Volume = 0.4f;
        player.Play();
        player.Pause();

        Assert.NotNull(sharedOutput);
        Assert.True(
            sharedOutput.IsPullLoopIdle,
            "Pause must join the pull loop so Shutdown cannot time out before exclusive create.");
        Assert.Equal(1, Volatile.Read(ref sharedCreateCount));
        Assert.Equal(0, Volatile.Read(ref exclusiveCreateCount));

        var exclusiveError = await Assert.ThrowsAsync<AudioOutputException>(() => player.ConfigureOutputAsync(
            new AudioOutputConfiguration
            {
                Mode = AudioOutputMode.Exclusive,
                ExclusiveFallback = ExclusiveModeFallback.None,
            }));

        Assert.True(
            Volatile.Read(ref exclusiveCreateCount) >= 1,
            "Exclusive factory must be invoked; Unspecified usually means Shutdown failed first.");
        Assert.Equal(AudioOutputFailureKind.DeviceBusy, exclusiveError.FailureKind);
        Assert.True(player.HasSource);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(OutputRecoveryIntent.RemainPaused, player.OutputRecoveryIntent);
        Assert.Equal(0.4f, player.Volume);
        Assert.Null(player.EffectiveOutputMode);
        Assert.Equal(OutputDeviceStatus.Unavailable, player.OutputDeviceStatus);
        Assert.Equal(ExclusiveFallbackReason.DeviceBusy, player.OutputFallbackReason);
        Assert.Equal(AudioOutputMode.Exclusive, player.OutputConfiguration.Mode);

        // Retry with fallback succeeds without reload.
        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.Shared,
        });

        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
        Assert.Equal(ExclusiveFallbackReason.DeviceBusy, player.OutputFallbackReason);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
        Assert.Equal(PlaybackState.Paused, player.State);
    }

    [Fact]
    public async Task Exclusive_InitFailure_FallsBackOnlyAfterQuiesce()
    {
        var shutdownOrder = new List<string>();
        var count = 0;
        var outputs = new FakeAudioOutputFactory((req, _) =>
        {
            count++;
            if (count == 1)
            {
                return new FakeAudioOutput(endpointId: "a");
            }

            if (req.Mode == AudioOutputMode.Exclusive)
            {
                return new InitFailFakeAudioOutput("ex", () => shutdownOrder.Add("exclusive-shutdown"));
            }

            shutdownOrder.Add("shared-create");
            return new FakeAudioOutput(endpointId: "shared");
        });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.Shared,
        });

        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
        Assert.Equal(ExclusiveFallbackReason.InitializationFailed, player.OutputFallbackReason);
        Assert.True(shutdownOrder.IndexOf("exclusive-shutdown") < shutdownOrder.IndexOf("shared-create"));
    }

    [Fact]
    public async Task Cancellation_DoesNotTriggerFallback()
    {
        using var cts = new CancellationTokenSource();
        var outputs = new FakeAudioOutputFactory((req, ct) =>
        {
            if (req.Mode == AudioOutputMode.Exclusive)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return new FakeAudioOutput(endpointId: req.DeviceId ?? "a");
        });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        var createdAfterLoad = outputs.Created.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player.ConfigureOutputAsync(
            new AudioOutputConfiguration
            {
                Mode = AudioOutputMode.Exclusive,
                ExclusiveFallback = ExclusiveModeFallback.Shared,
            },
            cts.Token));

        // Cancellation must not create a shared fallback after the exclusive attempt.
        Assert.DoesNotContain(
            outputs.Created.Skip(createdAfterLoad),
            c => c.Request.Mode == AudioOutputMode.Shared);
    }

    [Fact]
    public async Task SwitchBackToShared_Works()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        await player.ConfigureOutputAsync(new AudioOutputConfiguration { Mode = AudioOutputMode.Exclusive });
        await player.ConfigureOutputAsync(new AudioOutputConfiguration { Mode = AudioOutputMode.Shared });

        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
        Assert.Equal(ExclusiveFallbackReason.None, player.OutputFallbackReason);
    }

    [Fact]
    public async Task Exclusive_Stop_ThenReloadInitFailure_DoesNotLeaveDisposedOutputOrBlockShared()
    {
        // Reproduces: exclusive play → stop → reload hits DEVICE_IN_USE-style Init failure →
        // Seek must not throw ObjectDisposedException, and Shared configure/reload must recover.
        var createCount = 0;
        var outputs = new FakeAudioOutputFactory((req, _) =>
        {
            createCount++;
            // First successful exclusive session (initial shared load + exclusive configure).
            if (createCount <= 2)
            {
                return new FakeAudioOutput(endpointId: req.DeviceId ?? "a");
            }

            if (req.Mode == AudioOutputMode.Exclusive)
            {
                return new InitFailFakeAudioOutput("busy-ex", () => { });
            }

            return new FakeAudioOutput(endpointId: "shared-recovery");
        });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });
        player.Play();
        player.Stop();

        var loadEx = await Assert.ThrowsAsync<AudioOutputException>(() => player.LoadAsync("track2.wav"));
        Assert.Equal(AudioOutputFailureKind.InitializationFailed, loadEx.FailureKind);
        Assert.False(player.HasSource);
        Assert.Null(player.EffectiveOutputMode);

        // Previously this threw ObjectDisposedException from Pause on a zombie output.
        var seekEx = Record.Exception(() => player.Seek(TimeSpan.FromMilliseconds(500)));
        Assert.IsType<InvalidOperationException>(seekEx);

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Shared,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });
        Assert.Equal(AudioOutputMode.Shared, player.OutputConfiguration.Mode);

        await player.LoadAsync("track3.wav");
        Assert.True(player.HasSource);
        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
    }

    [Fact]
    public async Task SharedToExclusive_WhilePlaying_Succeeds()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });

        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.True(player.HasSource);
    }

    [Fact]
    public async Task SharedToExclusive_WhilePaused_Succeeds()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();
        player.Pause();

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });

        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.Equal(PlaybackState.Paused, player.State);
    }

    [Fact]
    public async Task StrictExclusive_CreateFailure_WhilePlaying_LeavesRecoverableState()
    {
        var outputs = new FakeAudioOutputFactory((req, _) =>
        {
            if (req.Mode == AudioOutputMode.Exclusive)
            {
                throw new AudioOutputException("busy", AudioOutputFailureKind.DeviceBusy);
            }

            return new FakeAudioOutput(endpointId: req.DeviceId ?? "a");
        });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        var ex = await Assert.ThrowsAsync<AudioOutputException>(() => player.ConfigureOutputAsync(
            new AudioOutputConfiguration
            {
                Mode = AudioOutputMode.Exclusive,
                ExclusiveFallback = ExclusiveModeFallback.None,
            }));

        Assert.Equal(AudioOutputFailureKind.DeviceBusy, ex.FailureKind);
        Assert.True(player.HasSource);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(OutputRecoveryIntent.ResumePlaying, player.OutputRecoveryIntent);
        Assert.Null(player.EffectiveOutputMode);
        Assert.Equal(OutputDeviceStatus.Unavailable, player.OutputDeviceStatus);
        Assert.Equal(AudioOutputMode.Exclusive, player.OutputConfiguration.Mode);
    }

    [Fact]
    public async Task ExclusiveCreateFailure_WithSharedFallback_WhilePlaying_ResumesOnShared()
    {
        var outputs = new FakeAudioOutputFactory((req, _) =>
        {
            if (req.Mode == AudioOutputMode.Exclusive)
            {
                throw new AudioOutputException("busy", AudioOutputFailureKind.DeviceBusy);
            }

            return new FakeAudioOutput(endpointId: req.DeviceId ?? "a");
        });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.Shared,
        });

        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
        Assert.Equal(ExclusiveFallbackReason.DeviceBusy, player.OutputFallbackReason);
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task ExclusiveToShared_WhilePlaying_Succeeds()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        await player.ConfigureOutputAsync(new AudioOutputConfiguration { Mode = AudioOutputMode.Exclusive });
        player.Play();

        await player.ConfigureOutputAsync(new AudioOutputConfiguration { Mode = AudioOutputMode.Shared });

        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task Exclusive_Pause_Resume_DoesNotReplayConsumedPcm()
    {
        FakeAudioOutput? exclusive = null;
        var outputs = new FakeAudioOutputFactory((req, _) =>
        {
            var output = new FakeAudioOutput(endpointId: req.DeviceId ?? "a", blockSamples: 32);
            if (req.Mode == AudioOutputMode.Exclusive)
            {
                exclusive = output;
            }

            return output;
        });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });

        player.Play();
        await WaitForAsync(() => exclusive is { ReadCount: > 4 }, TimeSpan.FromSeconds(2));
        Assert.NotNull(exclusive);

        player.Pause();
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.True(exclusive.IsPullLoopIdle);

        var readsAtPause = exclusive.ReadCount;
        int capturedAtPause;
        lock (exclusive.CapturedSamples)
        {
            capturedAtPause = exclusive.CapturedSamples.Count;
        }

        await Task.Delay(80);
        Assert.Equal(readsAtPause, exclusive.ReadCount);
        lock (exclusive.CapturedSamples)
        {
            Assert.Equal(capturedAtPause, exclusive.CapturedSamples.Count);
        }

        player.Play();
        await WaitForAsync(() => exclusive.ReadCount > readsAtPause, TimeSpan.FromSeconds(2));
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.True(exclusive.ReadCount > readsAtPause);
    }

    [Fact]
    public async Task Exclusive_RepeatedPauseResume_RemainsStable()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        await player.ConfigureOutputAsync(new AudioOutputConfiguration { Mode = AudioOutputMode.Exclusive });
        player.Play();

        for (var i = 0; i < 5; i++)
        {
            player.Pause();
            Assert.Equal(PlaybackState.Paused, player.State);
            player.Play();
            Assert.Equal(PlaybackState.Playing, player.State);
        }

        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.True(player.HasSource);
    }

    [Fact]
    public async Task Pause_ImmediatelyAfterSharedToExclusive_PreservesPausedState()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });
        player.Pause();

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);

        player.Play();
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task Stop_WhilePausedExclusive_ResetsTransport()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        await player.ConfigureOutputAsync(new AudioOutputConfiguration { Mode = AudioOutputMode.Exclusive });
        player.Play();
        player.Pause();
        player.Stop();

        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.True(player.Position < TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task RapidModeChanges_SettleOnLastRequest()
    {
        using var engine = CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        var tasks = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            var exclusive = i % 2 == 0;
            tasks.Add(player.ConfigureOutputAsync(new AudioOutputConfiguration
            {
                Mode = exclusive ? AudioOutputMode.Exclusive : AudioOutputMode.Shared,
                ExclusiveFallback = ExclusiveModeFallback.None,
            }));
        }

        // Intermediate requests are cancelled when a newer switch supersedes them.
        var results = await Task.WhenAll(tasks.Select(async t =>
        {
            try
            {
                await t.ConfigureAwait(false);
                return "ok";
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
        }));

        Assert.Contains("ok", results);
        Assert.True(player.HasSource);
        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode); // last request (i=7)
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task StrictFailure_ThenSharedRecovery_RestoresPlayback()
    {
        var failExclusive = true;
        var outputs = new FakeAudioOutputFactory((req, _) =>
        {
            if (req.Mode == AudioOutputMode.Exclusive && failExclusive)
            {
                throw new AudioOutputException("busy", AudioOutputFailureKind.DeviceBusy);
            }

            return new FakeAudioOutput(endpointId: req.DeviceId ?? "a");
        });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        await Assert.ThrowsAsync<AudioOutputException>(() => player.ConfigureOutputAsync(
            new AudioOutputConfiguration
            {
                Mode = AudioOutputMode.Exclusive,
                ExclusiveFallback = ExclusiveModeFallback.None,
            }));

        failExclusive = false;
        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Shared,
        });

        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
    }

    [Fact]
    public async Task DisposeAsync_DuringConfigure_DoesNotHang()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputs = new FakeAudioOutputFactory(async (req, ct) =>
        {
            if (req.Mode == AudioOutputMode.Exclusive)
            {
                started.TrySetResult();
                // Async wait for DisposeAsync to cancel — sync WaitOne blocks the pool.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    throw;
                }

                throw new TimeoutException("Configure was not cancelled by DisposeAsync.");
            }

            return new FakeAudioOutput(endpointId: req.DeviceId ?? "a");
        });

        var engine = CreateEngine(outputs: outputs);
        var player = engine.CreatePlayer();
        try
        {
            await player.LoadAsync("track.wav");
            player.Play();

            var configure = player.ConfigureOutputAsync(new AudioOutputConfiguration
            {
                Mode = AudioOutputMode.Exclusive,
                ExclusiveFallback = ExclusiveModeFallback.None,
            });
            // Exclusive rebind runs AssistWasapiEndpointRelease + 1s grace before Create; under
            // parallel CI that can exceed a tight 2s budget before started is signaled.
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await player.DisposeAsync();
            await engine.DisposeAsync();

            await Assert.ThrowsAnyAsync<Exception>(async () => await configure.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await player.DisposeAsync();
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProbeFailure_Unsupported_StrictThrows()
    {
        var outputs = new FakeAudioOutputFactory(
            (req, _) => new FakeAudioOutput(endpointId: req.DeviceId ?? "a"),
            (req, preferred, _) =>
            {
                if (req.Mode == AudioOutputMode.Exclusive)
                {
                    return new AudioOutputFormatProbeResult(
                        false,
                        preferred,
                        AudioOutputFailureKind.UnsupportedFormat);
                }

                return new AudioOutputFormatProbeResult(true, preferred);
            });

        using var engine = CreateEngine(outputs: outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        var ex = await Assert.ThrowsAsync<AudioOutputException>(() => player.ConfigureOutputAsync(
            new AudioOutputConfiguration
            {
                Mode = AudioOutputMode.Exclusive,
                ExclusiveFallback = ExclusiveModeFallback.None,
            }));

        Assert.Equal(AudioOutputFailureKind.UnsupportedFormat, ex.FailureKind);
        Assert.True(player.HasSource);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeout.TotalMilliseconds)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(5);
        }
    }

    private static AudioEngine CreateEngine(FakeAudioOutputFactory? outputs = null)
    {
        var devices = new FakeAudioOutputDevices(
            new[]
            {
                new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active),
            },
            "a");
        outputs ??= new FakeAudioOutputFactory(id => new FakeAudioOutput(endpointId: id ?? "a"));
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2));
        return new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero },
            devices);
    }

    private sealed class InitFailFakeAudioOutput : FakeAudioOutput
    {
        private readonly Action _onShutdown;

        public InitFailFakeAudioOutput(string endpointId, Action onShutdown)
            : base(endpointId: endpointId)
        {
            _onShutdown = onShutdown;
        }

        public override void Shutdown()
        {
            _onShutdown();
            base.Shutdown();
        }

        public override void Initialize(IPcmSampleSource source)
        {
            throw new AudioOutputException(
                "init failed",
                AudioOutputFailureKind.InitializationFailed);
        }
    }
}
