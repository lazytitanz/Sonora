using Sonora.Audio.Abstractions;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Regression for the manual Duration+10 stall dump: sticky terminal EOS after an empty
/// promote, Playing with a stopped output while next is Ready, and Seek stuck after SeekBegin
/// because OnOutputStopped joined the pull thread under _gate.
/// </summary>
public sealed class StickyEosGaplessTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public async Task PastEndSeeks_EmptyPromote_ThenReadyNext_RecoversWithoutStickyPlayingSilence()
    {
        // A (short) → B (empty completed ring) → C (playable).
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            IAudioDecoder decoder = path switch
            {
                "a.wav" => FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(200)),
                "b.wav" => new FakeAudioDecoder(Format, Array.Empty<float>()),
                _ => FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(800)),
            };
            return Task.FromResult(decoder);
        });

        FakeAudioOutput? output = null;
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 64, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                BufferDuration = TimeSpan.FromMilliseconds(50),
            });
        using var player = engine.CreatePlayer();

        var transitions = 0;
        var preloadC = 0;
        player.TrackTransitioned += (_, e) =>
        {
            Interlocked.Increment(ref transitions);
            if (e.NextPath == "b.wav" && Interlocked.Exchange(ref preloadC, 1) == 0)
            {
                // Preload C as soon as empty B is promoted (host queue advance).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await player.PreloadNextAsync("c.wav").ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignored — assertion below waits for source path
                    }
                });
            }
        };

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();

        // Repeated past-end seeks only while still on A (Duration+10). Seeking past end after
        // landing on C with no further next would legitimately complete the session.
        for (var i = 0; i < 3; i++)
        {
            if (!string.Equals(player.SourcePath, "a.wav", StringComparison.Ordinal))
            {
                break;
            }

            player.SetNextSeekCorrelationId(100 + i);
            try
            {
                _ = player.SeekWithResult(player.Duration + TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException)
            {
                // superseded during promote window
            }

            await Task.Delay(40);
        }

        await WaitForAsync(() => Volatile.Read(ref transitions) >= 1, TimeSpan.FromSeconds(8));
        await WaitForAsync(
            () => string.Equals(player.SourcePath, "c.wav", StringComparison.Ordinal),
            TimeSpan.FromSeconds(8));

        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.NotNull(output);
        await WaitForAsync(() => !output!.IsPullLoopIdle, TimeSpan.FromSeconds(5));

        var diag = player.GetDiagnostics();
        Assert.True(
            diag.LastSilenceReason is not SeekSilenceReason.EndOfStream
            || diag.SamplesConsumed > 0
            || diag.PumpRunning,
            $"Sticky EOS limbo: silence={diag.LastSilenceReason}, consumed={diag.SamplesConsumed}, pump={diag.PumpRunning}");

        // Subsequent seek must complete (SeekBegin → ArmBarrier/terminal) without gate deadlock.
        player.SetNextSeekCorrelationId(999);
        await Task.Run(() => player.SeekWithResult(TimeSpan.FromMilliseconds(40)))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var buffer = new SeekLifecycleEvent[256];
        var n = player.CopySeekLifecycleTrace(buffer, out _, out _);
        var events = buffer.AsSpan(0, n).ToArray();
        Assert.Contains(events, e => e.SeekId == 999 && e.Stage == SeekLifecycleStage.SeekBegin);
        Assert.Contains(events, e => e.SeekId == 999 && e.Stage == SeekLifecycleStage.ArmBarrier);
        Assert.Contains(
            events,
            e => e.SeekId == 999
                 && e.Stage is SeekLifecycleStage.Commit or SeekLifecycleStage.Superseded or SeekLifecycleStage.Failed);

        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.False(output.IsPullLoopIdle);
        Assert.Equal(0, player.GetDiagnostics().SeekInProgress);
    }

    [Fact]
    public async Task EosWhileNextLoading_ThenReady_RestartsOutputWithoutPlayingSilenceLimbo()
    {
        var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeAudioDecoderFactory(async (path, ct) =>
        {
            if (path == "a.wav")
            {
                return FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(100));
            }

            await releaseNext.Task.WaitAsync(ct).ConfigureAwait(false);
            return FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(600));
        });

        FakeAudioOutput? output = null;
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 64, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                BufferDuration = TimeSpan.FromMilliseconds(40),
            });
        using var player = engine.CreatePlayer();
        var transitions = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);

        await player.LoadAsync("a.wav");
        var preload = player.PreloadNextAsync("b.wav");
        await WaitForAsync(() => player.NextTrackState == NextTrackState.Loading, TimeSpan.FromSeconds(2));
        player.Play();

        // Drain A while next is still Loading.
        player.Seek(player.Duration + TimeSpan.FromSeconds(10));

        await WaitForAsync(
            () => player.GetDiagnostics().LastSilenceReason == SeekSilenceReason.PendingNext
                  || Volatile.Read(ref transitions) >= 1
                  || output is { IsPullLoopIdle: true },
            TimeSpan.FromSeconds(5));

        // Next becomes Ready after the output may have stopped pulling.
        releaseNext.TrySetResult();
        await preload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(
            player.NextTrackState is NextTrackState.Ready or NextTrackState.None,
            $"Unexpected next state {player.NextTrackState}");

        await WaitForAsync(() => Volatile.Read(ref transitions) >= 1, TimeSpan.FromSeconds(8));
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.NotNull(output);
        await WaitForAsync(() => !output!.IsPullLoopIdle, TimeSpan.FromSeconds(5));

        var consumed0 = player.GetDiagnostics().SamplesConsumed;
        await Task.Delay(200);
        var diag = player.GetDiagnostics();
        Assert.True(
            diag.SamplesConsumed > consumed0
            || diag.LastSilenceReason is SeekSilenceReason.None or SeekSilenceReason.Underrun,
            $"Expected progress after recover: consumed {consumed0}->{diag.SamplesConsumed}, silence={diag.LastSilenceReason}");

        player.SetNextSeekCorrelationId(42);
        await Task.Run(() => player.SeekWithResult(TimeSpan.FromMilliseconds(10)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, player.GetDiagnostics().SeekInProgress);
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task OutputStoppedEos_WithNextReady_DoesNotDeadlockSubsequentSeek()
    {
        // Directly exercises the Seek-16 failure mode: Stopped raised from the pull thread must not
        // take _gate and Join the same thread.
        var factory = new FakeAudioDecoderFactory((path, _) =>
            Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(path == "a.wav" ? 80 : 400))));

        FakeAudioOutput? output = null;
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 32, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero, BufferDuration = TimeSpan.FromMilliseconds(30) });
        using var player = engine.CreatePlayer();
        var transitions = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        player.Play();

        player.Seek(player.Duration + TimeSpan.FromSeconds(10));
        await WaitForAsync(() => Volatile.Read(ref transitions) >= 1, TimeSpan.FromSeconds(5));

        Assert.NotNull(output);
        player.OnOutputStopped(output, new AudioOutputStoppedEventArgs(reachedEndOfStream: true));

        var seekDone = Task.Run(() =>
        {
            player.SetNextSeekCorrelationId(77);
            return player.SeekWithResult(TimeSpan.FromMilliseconds(25));
        });
        await seekDone.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(0, player.GetDiagnostics().SeekInProgress);

        var buffer = new SeekLifecycleEvent[128];
        var n = player.CopySeekLifecycleTrace(buffer, out _, out _);
        var events = buffer.AsSpan(0, n).ToArray();
        Assert.Contains(events, e => e.SeekId == 77 && e.Stage == SeekLifecycleStage.ArmBarrier);
        Assert.True(
            events.Any(e => e.Stage == SeekLifecycleStage.GaplessRecover) || !output!.IsPullLoopIdle,
            "Expected gapless recover and/or a live pull loop after EOS Stopped with next Ready.");
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

            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition not met before timeout.");
    }
}
