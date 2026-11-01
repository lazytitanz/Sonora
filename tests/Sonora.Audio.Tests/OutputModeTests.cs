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
