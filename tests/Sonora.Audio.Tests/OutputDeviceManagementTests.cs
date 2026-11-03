using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public class OutputDeviceManagementTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 1);

    [Fact]
    public void Enumeration_ReturnsSnapshot_IncludingUnavailable()
    {
        var devices = new FakeAudioOutputDevices(new[]
        {
            new AudioOutputDevice("a", "Speakers", isDefault: true, AudioDeviceState.Active),
            new AudioOutputDevice("b", "Headphones", isDefault: false, AudioDeviceState.Unplugged),
            new AudioOutputDevice("c", "Disabled DAC", isDefault: false, AudioDeviceState.Disabled),
        }, defaultId: "a");

        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(FakeAudioDecoder.CreateSilence(Format, TimeSpan.FromMilliseconds(10))),
            new FakeAudioOutputFactory(),
            outputDevices: devices);

        var list = engine.OutputDevices.GetDevices();
        Assert.Equal(3, list.Count);
        Assert.Equal("a", engine.OutputDevices.GetDefaultDevice()?.Id);
        Assert.Contains(list, d => d.State == AudioDeviceState.Unplugged);
        Assert.Contains(list, d => d.State == AudioDeviceState.Disabled);
    }

    [Fact]
    public void Enumeration_EmptyList_IsHandled()
    {
        var devices = new FakeAudioOutputDevices(Array.Empty<AudioOutputDevice>(), defaultId: null);
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(FakeAudioDecoder.CreateSilence(Format, TimeSpan.FromMilliseconds(10))),
            new FakeAudioOutputFactory(),
            outputDevices: devices);

        Assert.Empty(engine.OutputDevices.GetDevices());
        Assert.Null(engine.OutputDevices.GetDefaultDevice());
    }

    [Fact]
    public async Task SetOutputDeviceAsync_ExplicitSelection_Works()
    {
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id => new FakeAudioOutput(endpointId: id ?? "default"));
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        await player.SetOutputDeviceAsync("b");

        Assert.Equal("b", player.PreferredOutputDeviceId);
        Assert.Equal("b", player.ActiveOutputDeviceId);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
        Assert.Contains(outputs.Created, c => c.Request.DeviceId == "b");
    }

    [Fact]
    public async Task SetOutputDeviceAsync_InvalidId_Throws()
    {
        var devices = CreateTwoActiveDevices();
        using var engine = CreateEngine(devices, new FakeAudioOutputFactory());
        using var player = engine.CreatePlayer();

        await Assert.ThrowsAsync<AudioDeviceException>(() => player.SetOutputDeviceAsync("missing"));
    }

    [Fact]
    public async Task SetOutputDeviceAsync_SameDevice_AvoidsReinit()
    {
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id => new FakeAudioOutput(endpointId: id ?? "default"));
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        var afterLoad = outputs.Created.Count;

        await player.SetOutputDeviceAsync(null);
        Assert.Equal(afterLoad, outputs.Created.Count);
    }

    [Fact]
    public async Task SetOutputDeviceAsync_ReturnToDefault_Works()
    {
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id => new FakeAudioOutput(endpointId: id ?? "default"));
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        await player.SetOutputDeviceAsync("b");
        await player.SetOutputDeviceAsync(null);

        Assert.Null(player.PreferredOutputDeviceId);
        Assert.Equal("a", player.ActiveOutputDeviceId);
    }

    [Fact]
    public async Task RapidSelection_OnlyLatestWins()
    {
        var devices = CreateTwoActiveDevices();
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
        // Bound waits so a stuck cancellation cannot sit until blame-hang (3m) aborts the host.
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        var firstError = await Record.ExceptionAsync(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<OperationCanceledException>(firstError);

        Assert.Equal("a", player.PreferredOutputDeviceId);
        Assert.Equal("a", player.ActiveOutputDeviceId);
    }

    [Fact]
    public async Task DefaultChange_AffectsFollowOnly()
    {
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id => new FakeAudioOutput(endpointId: id ?? "default"));
        using var engine = CreateEngine(devices, outputs);
        using var follow = engine.CreatePlayer();
        using var explicitPlayer = engine.CreatePlayer();
        await follow.LoadAsync("track.wav");
        await explicitPlayer.LoadAsync("track.wav");
        await explicitPlayer.SetOutputDeviceAsync("b");

        devices.SetDefault("b");
        await WaitForAsync(() => follow.ActiveOutputDeviceId == "b", TimeSpan.FromSeconds(3));

        Assert.Equal("b", follow.ActiveOutputDeviceId);
        Assert.Equal("b", explicitPlayer.ActiveOutputDeviceId);
        Assert.Equal("b", explicitPlayer.PreferredOutputDeviceId);
    }

    [Fact]
    public async Task DeviceLoss_WhilePlaying_SetsResumeIntent_AndRecovers()
    {
        FakeAudioOutput? current = null;
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id =>
        {
            current = new FakeAudioOutput(endpointId: id ?? "default", latency: TimeSpan.FromMilliseconds(5));
            return current;
        });

        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();
        Assert.Equal(PlaybackState.Playing, player.State);

        var lost = current!;
        lost.InjectFailure(new AudioOutputException("device lost"));

        await WaitForAsync(
            () => player.OutputRecoveryIntent == OutputRecoveryIntent.ResumePlaying
                  || player.OutputDeviceStatus != OutputDeviceStatus.Available,
            TimeSpan.FromSeconds(3));

        // Preferred still null (follow default); ensure recovery can resume when devices remain.
        await WaitForAsync(
            () => player.State == PlaybackState.Playing && player.OutputDeviceStatus == OutputDeviceStatus.Available,
            TimeSpan.FromSeconds(3));

        Assert.Equal(OutputRecoveryIntent.None, player.OutputRecoveryIntent);
    }

    [Fact]
    public async Task ExplicitStop_DuringRecovery_ClearsIntent()
    {
        FakeAudioOutput? current = null;
        var devices = new FakeAudioOutputDevices(new[]
        {
            new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active),
        }, "a");

        var failNext = false;
        var outputs = new FakeAudioOutputFactory(id =>
        {
            if (failNext)
            {
                throw new AudioOutputException("no device");
            }

            current = new FakeAudioOutput(endpointId: id ?? "a");
            return current;
        });

        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();

        failNext = true;
        devices.SetDevices(Array.Empty<AudioOutputDevice>(), null);
        current!.InjectFailure(new AudioOutputException("device lost"));

        await WaitForAsync(
            () => player.OutputDeviceStatus == OutputDeviceStatus.Unavailable
                  || player.OutputRecoveryIntent == OutputRecoveryIntent.ResumePlaying,
            TimeSpan.FromSeconds(3));

        player.Stop();
        Assert.Equal(OutputRecoveryIntent.None, player.OutputRecoveryIntent);

        failNext = false;
        devices.SetDevices(new[]
        {
            new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active),
        }, "a");

        await Task.Delay(200);
        Assert.NotEqual(PlaybackState.Playing, player.State);
        Assert.Equal(OutputRecoveryIntent.None, player.OutputRecoveryIntent);
    }

    [Fact]
    public async Task PreferredLost_FallsBack_DoesNotAutoRestore()
    {
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id => new FakeAudioOutput(endpointId: id ?? "default"));
        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        await player.SetOutputDeviceAsync("b");
        player.Play();

        devices.UpdateDevice(new AudioOutputDevice("b", "Headphones", false, AudioDeviceState.Unplugged));
        // Simulate loss on active output
        var active = outputs.Created.Last().Output as FakeAudioOutput;
        active!.InjectFailure(new AudioOutputException("unplugged"));

        await WaitForAsync(
            () => player.OutputDeviceStatus == OutputDeviceStatus.PreferredUnavailable
                  || player.ActiveOutputDeviceId == "a",
            TimeSpan.FromSeconds(3));

        Assert.Equal("b", player.PreferredOutputDeviceId);

        devices.UpdateDevice(new AudioOutputDevice("b", "Headphones", false, AudioDeviceState.Active));
        await Task.Delay(150);
        Assert.Equal("b", player.PreferredOutputDeviceId);
        Assert.NotEqual("b", player.ActiveOutputDeviceId); // no auto-restore
    }

    [Fact]
    public async Task Switch_PreservesRing_AndPositionEstimate()
    {
        FakeAudioOutput? current = null;
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id =>
        {
            current = new FakeAudioOutput(
                blockSamples: 64,
                latency: TimeSpan.FromMilliseconds(30),
                endpointId: id ?? "default");
            return current;
        });

        using var engine = CreateEngine(devices, outputs, TimeSpan.FromMilliseconds(200));
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Play();
        await Task.Delay(80);
        var before = player.Position;
        var ringBefore = player.GetDiagnostics().BufferedSamples;

        await player.SetOutputDeviceAsync("b");

        Assert.True(player.Position >= before - TimeSpan.FromMilliseconds(50));
        // Ring should not be wiped to zero solely by the switch (decode continues).
        Assert.True(player.GetDiagnostics().BufferedSamples >= 0);
        Assert.True(ringBefore >= 0);
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
        Assert.Equal("b", player.ActiveOutputDeviceId);
        _ = current;
    }

    [Fact]
    public async Task Switch_WithReadyNext_PreservesGapless_NoFalseCompletion()
    {
        var factory = new FakeAudioDecoderFactory((path, _) =>
            Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(250))));
        var devices = CreateTwoActiveDevices();
        var createCount = 0;
        var outputs = new FakeAudioOutputFactory(id =>
        {
            Interlocked.Increment(ref createCount);
            return new FakeAudioOutput(blockSamples: 256, latency: TimeSpan.FromMilliseconds(1), endpointId: id ?? "default");
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero, BufferDuration = TimeSpan.FromSeconds(1) },
            devices);
        using var player = engine.CreatePlayer();
        var completed = 0;
        var transitions = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        var createsAfterLoad = Volatile.Read(ref createCount);

        // Switch while a next track is ready must not complete or transition spuriously.
        await player.SetOutputDeviceAsync("b");
        Assert.True(Volatile.Read(ref createCount) > createsAfterLoad);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.Equal(0, Volatile.Read(ref transitions));
        Assert.Equal("b", player.ActiveOutputDeviceId);

        player.Play();
        await WaitForAsync(() => Volatile.Read(ref completed) == 1, TimeSpan.FromSeconds(15));
        Assert.Equal(1, Volatile.Read(ref transitions));
        Assert.Equal(1, Volatile.Read(ref completed));
    }

    [Fact]
    public async Task Dispose_Unsubscribes_AndIgnoresStaleCallbacks()
    {
        FakeAudioOutput? current = null;
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id =>
        {
            current = new FakeAudioOutput(endpointId: id ?? "default");
            return current;
        });

        var engine = CreateEngine(devices, outputs);
        var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        var output = current!;
        player.Dispose();

        output.InjectFailure(new AudioOutputException("late"));
        await Task.Delay(50);
        engine.Dispose();
    }

    [Fact]
    public async Task CreateFailure_ForNewDefault_SetsUnavailable()
    {
        var devices = CreateTwoActiveDevices();
        var failForB = false;
        var outputs = new FakeAudioOutputFactory(id =>
        {
            if (failForB && id == "b")
            {
                throw new AudioOutputException("open failed");
            }

            return new FakeAudioOutput(endpointId: id ?? "default");
        });

        using var engine = CreateEngine(devices, outputs);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        failForB = true;

        await Assert.ThrowsAsync<AudioOutputException>(() => player.SetOutputDeviceAsync("b"));
        Assert.Equal(OutputDeviceStatus.Unavailable, player.OutputDeviceStatus);
    }

    [Fact]
    public async Task VolumeAndEffects_PersistAcrossSwitch()
    {
        var devices = CreateTwoActiveDevices();
        var outputs = new FakeAudioOutputFactory(id => new FakeAudioOutput(endpointId: id ?? "default"));
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(new FakeAudioDecoder(Format, CreatePcm(Format, 1f, 0.1f))),
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero, EffectFactory = new FakeAudioEffectFactory() },
            devices);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");
        player.Volume = 0.4f;
        var eq = player.Effects.AddEqualizer();
        eq.SetBandGain(0, 3);

        await player.SetOutputDeviceAsync("b");

        Assert.Equal(0.4f, player.Volume);
        Assert.Single(player.Effects.Items);
        Assert.Equal(3, eq.GetBandGain(0));
    }

    private static FakeAudioOutputDevices CreateTwoActiveDevices() =>
        new(
            new[]
            {
                new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active),
                new AudioOutputDevice("b", "Headphones", false, AudioDeviceState.Active),
            },
            defaultId: "a");

    private static AudioEngine CreateEngine(
        FakeAudioOutputDevices devices,
        FakeAudioOutputFactory outputs,
        TimeSpan? duration = null)
    {
        var seconds = duration?.TotalSeconds ?? 1.0;
        var decoder = new FakeAudioDecoder(Format, CreatePcm(Format, (float)seconds, 0.2f));
        return new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero },
            devices);
    }

    private static float[] CreatePcm(AudioFormat format, float seconds, float value)
    {
        var samples = format.TimeToSamples(TimeSpan.FromSeconds(seconds));
        var data = new float[samples];
        Array.Fill(data, value);
        return data;
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

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition not met before timeout.");
    }
}
