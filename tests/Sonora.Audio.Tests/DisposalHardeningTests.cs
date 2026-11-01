using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Deterministic Dispose / DisposeAsync overlap coverage. Uses holdable fakes — no correctness sleeps.
/// </summary>
public sealed class DisposalHardeningTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public async Task Dispose_WhileLoadCreateHeld_DoesNotLeaveActiveConsumer()
    {
        var createEntered = new ManualResetEventSlim(false);
        var allowCreate = new ManualResetEventSlim(false);
        var outputs = new List<HoldableFakeAudioOutput>();

        var decoderFactory = new FakeAudioDecoderFactory(async (_, ct) =>
        {
            createEntered.Set();
            while (!allowCreate.IsSet)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            return FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(200));
        });

        var outputFactory = new FakeAudioOutputFactory(_ =>
        {
            var o = new HoldableFakeAudioOutput("a");
            outputs.Add(o);
            return o;
        });

        using var engine = new AudioEngine(
            decoderFactory,
            outputFactory,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        var player = engine.CreatePlayer();

        var loadTask = player.LoadAsync("tone.wav");
        Assert.True(createEntered.Wait(TimeSpan.FromSeconds(2)));

        // Dispose while decoder open is still pending (off-gate).
        var disposeTask = Task.Run(player.Dispose);
        allowCreate.Set();

        try
        {
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException)
        {
            // Load observed dispose.
        }
        catch (OperationCanceledException)
        {
            // Generation / linked CTS cancelled.
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(outputs, o => Assert.True(o.IsPullLoopIdle));
        Assert.All(outputs, o => Assert.True(o.IsShutdown || o.InitializeCount == 0));
    }

    [Fact]
    public async Task DisposeAsync_WhileOutputReadHeld_CompletesSingleShutdown()
    {
        var decoder = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(3)));
        HoldableFakeAudioOutput? output = null;
        var outputFactory = new FakeAudioOutputFactory(_ =>
        {
            output = new HoldableFakeAudioOutput("a");
            return output;
        });

        await using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            outputFactory,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        var player = engine.CreatePlayer();
        await player.LoadAsync("tone.wav");
        player.Play();

        Assert.NotNull(output);
        output!.HoldRead();
        Assert.True(output.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        var disposeTask = player.DisposeAsync().AsTask();
        output.ReleaseRead();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(output.IsShutdown);
        Assert.True(output.IsPullLoopIdle);
        Assert.Equal(1, output.ShutdownCount);
        Assert.Throws<ObjectDisposedException>(() => player.Play());
    }

    [Fact]
    public async Task Dispose_WhenShutdownThrows_StillQuiescesOutput()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(1));
        HoldableFakeAudioOutput? output = null;
        var outputFactory = new FakeAudioOutputFactory(_ =>
        {
            output = new HoldableFakeAudioOutput("a")
            {
                ShutdownFault = () => new AudioOutputException("simulated shutdown failure"),
            };
            return output;
        });

        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            outputFactory,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        var player = engine.CreatePlayer();
        await player.LoadAsync("tone.wav");
        player.Play();
        Assert.NotNull(output);

        player.Dispose();
        Assert.True(output!.ShutdownCount > 0);
        Assert.True(output.IsPullLoopIdle);
    }

    [Fact]
    public async Task DisposeAsync_WhileDecoderHeldAwayFromEof_Completes()
    {
        // HoldableFakeDecoder starts held (silence) until Release — simulates stalled network decode.
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(hold),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        var player = engine.CreatePlayer();
        await player.LoadAsync("tone.wav");
        player.Play();

        var disposeTask = player.DisposeAsync().AsTask();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => player.Play());
    }

    [Fact]
    public async Task Engine_UnregisterDisposedPlayer_EngineDisposeIsSafe()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(100));
        var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });

        var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        await player.DisposeAsync();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DoesNotBlockGateAcrossHeldShutdown()
    {
        HoldableFakeAudioOutput? primaryOutput = null;
        var outputFactory = new FakeAudioOutputFactory(_ =>
        {
            var created = new HoldableFakeAudioOutput("a");
            primaryOutput ??= created;
            return created;
        });

        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory((_, _) =>
                Task.FromResult<IAudioDecoder>(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)))),
            outputFactory,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        var player = engine.CreatePlayer();
        await player.LoadAsync("tone.wav");
        player.Play();
        Assert.NotNull(primaryOutput);

        primaryOutput!.HoldShutdown();
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeTask = Task.Run(() =>
        {
            disposeStarted.TrySetResult();
            player.Dispose();
        });

        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(primaryOutput.WaitUntilShutdownHeld(TimeSpan.FromSeconds(2)));

        // Per-player gate must be free while Dispose waits off-gate on Shutdown.
        var sibling = engine.CreatePlayer();
        await sibling.LoadAsync("other.wav").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PlaybackState.Stopped, sibling.State);

        primaryOutput.ReleaseShutdown();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        sibling.Dispose();
    }
}
