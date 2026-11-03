using Sonora.Audio.Abstractions;
using Sonora.Audio.Buffers;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Seeking;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Ownership/teardown regressions: stop timeout must not authorize Seek/Dispose/restart races.
/// </summary>
public sealed class DecodePumpOwnershipTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(80);

    [Fact]
    public async Task Stop_TimesOut_WhileReadBlocked_WorkerStillOwned()
    {
        var blocking = new BlockingReadFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        try
        {
            var pipeline = CreatePipeline(blocking);
            pipeline.StartPump([], _ => { }, null);
            Assert.True(blocking.WaitUntilReadEntered(TimeSpan.FromSeconds(2)));

            await pipeline.StopPumpAsync(ShortWait);
            Assert.True(pipeline.LastStopTimedOut);
            Assert.True(pipeline.IsPumpRunning);
            Assert.False(pipeline.WorkerShutdownCompleted.IsCompleted);
            // Diagnostic flag — worker is still owned, not abandoned.
            Assert.True(pipeline.HasOrphanDecodeWorker);
            Assert.Equal(0, blocking.DisposeCount);
        }
        finally
        {
            blocking.ReleaseRead();
        }
    }

    [Fact]
    public async Task Seek_CannotEnterDecoder_WhileReadOutstanding()
    {
        var blocking = new BlockingReadFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(3)));
        try
        {
            using var engine = new AudioEngine(
                new FakeAudioDecoderFactory((_, _) => Task.FromResult<IAudioDecoder>(blocking)),
                new FakeAudioOutputFactory(),
                new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
            using var player = engine.CreatePlayer();
            await player.LoadAsync("a.wav");
            player.Play();
            Assert.True(blocking.WaitUntilReadEntered(TimeSpan.FromSeconds(2)));

            var seekTask = Task.Run(() => player.Seek(TimeSpan.FromMilliseconds(50)));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await seekTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("decode worker", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, blocking.SeekCount);
            Assert.Equal(PlaybackState.Stopped, player.State);
        }
        finally
        {
            blocking.ReleaseRead();
        }
    }

    [Fact]
    public async Task Dispose_DoesNotReleaseDecoder_UntilWorkerExits()
    {
        var blocking = new BlockingReadFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        TrackDecodePipeline? pipeline = null;
        try
        {
            pipeline = CreatePipeline(blocking);
            pipeline.StartPump([], _ => { }, null);
            Assert.True(blocking.WaitUntilReadEntered(TimeSpan.FromSeconds(2)));

            var disposeTask = pipeline.DisposeAsync();
            await Task.Delay(ShortWait);
            Assert.True(pipeline.IsDisposePending);
            Assert.Equal(0, blocking.DisposeCount);
            Assert.False(pipeline.ResourcesReleased);

            blocking.ReleaseRead();
            await disposeTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pipeline.ResourcesReleased);
            Assert.Equal(1, blocking.DisposeCount);
        }
        finally
        {
            blocking.ReleaseRead();
            if (pipeline is not null && !pipeline.ResourcesReleased)
            {
                try
                {
                    await pipeline.DisposeAsync();
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    [Fact]
    public async Task Restart_CannotCreateSecondWorker_WhileFirstAlive()
    {
        var blocking = new BlockingReadFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        try
        {
            var pipeline = CreatePipeline(blocking);
            pipeline.StartPump([], _ => { }, null);
            Assert.True(blocking.WaitUntilReadEntered(TimeSpan.FromSeconds(2)));
            await pipeline.StopPumpAsync(ShortWait);
            Assert.True(pipeline.IsPumpRunning);

            Assert.Throws<InvalidOperationException>(() =>
                pipeline.StartPump([], _ => { }, null));
        }
        finally
        {
            blocking.ReleaseRead();
        }
    }

    [Fact]
    public async Task Concurrent_StopAndDispose_CoordinateSafely()
    {
        var blocking = new BlockingReadFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        var pipeline = CreatePipeline(blocking);
        try
        {
            pipeline.StartPump([], _ => { }, null);
            Assert.True(blocking.WaitUntilReadEntered(TimeSpan.FromSeconds(2)));

            var stop = Task.Run(() => pipeline.StopPumpAsync(ShortWait));
            var dispose = Task.Run(async () => await pipeline.DisposeAsync());
            blocking.ReleaseRead();
            await Task.WhenAll(stop, dispose).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pipeline.ResourcesReleased);
            Assert.Equal(1, blocking.DisposeCount);
        }
        finally
        {
            blocking.ReleaseRead();
        }
    }

    [Fact]
    public async Task ReleasingBlockedRead_AllowsPendingCleanupToFinish()
    {
        var blocking = new BlockingReadFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        var pipeline = CreatePipeline(blocking);
        try
        {
            pipeline.StartPump([], _ => { }, null);
            Assert.True(blocking.WaitUntilReadEntered(TimeSpan.FromSeconds(2)));
            await pipeline.StopPumpAsync(ShortWait);
            Assert.False(pipeline.WorkerShutdownCompleted.IsCompleted);

            blocking.ReleaseRead();
            await pipeline.WorkerShutdownCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pipeline.IsPumpRunning);
            Assert.False(pipeline.HasOrphanDecodeWorker);
        }
        finally
        {
            blocking.ReleaseRead();
            await pipeline.DisposeAsync();
        }
    }

    [Fact]
    public async Task RingWriteCancellation_TerminatesWorker()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(5));
        var ring = new PcmRingBuffer(Format.ChannelCount * 8);
        var pipeline = new TrackDecodePipeline("t.wav", decoder, ring, decoder.Duration);
        pipeline.StartPump([], _ => { }, null);

        await Task.Delay(50);
        await pipeline.StopPumpAsync(TimeSpan.FromSeconds(2));
        await pipeline.WorkerShutdownCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pipeline.IsPumpRunning);
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task DecodeException_ProducesObservableTerminalState()
    {
        var failing = new ThrowingReadDecoder(Format);
        var pipeline = CreatePipeline(failing);
        Exception? observed = null;
        pipeline.StartPump([], _ => { }, ex => observed = ex);
        await pipeline.WorkerShutdownCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(observed);
        Assert.False(pipeline.IsPumpRunning);
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task DemotedPipeline_DisposeWaitsForWorker_WhileSuccessorRuns()
    {
        // Simulates gapless promote: demoted pipeline DisposeAsync must not release the decoder
        // until its worker exits, and must not block starting the successor pipeline.
        var blocking = new BlockingReadFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        var demoted = CreatePipeline(blocking);
        var successor = CreatePipeline(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(400)));
        try
        {
            demoted.StartPump([], _ => { }, null);
            Assert.True(blocking.WaitUntilReadEntered(TimeSpan.FromSeconds(2)));

            var disposeTask = demoted.DisposeAsync().AsTask();
            await Task.Delay(ShortWait);
            Assert.True(demoted.IsDisposePending);
            Assert.Equal(0, blocking.DisposeCount);

            successor.StartPump([], _ => { }, null);
            Assert.True(successor.IsPumpRunning);

            blocking.ReleaseRead();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(demoted.ResourcesReleased);
            Assert.Equal(1, blocking.DisposeCount);
            Assert.True(successor.IsPumpRunning);
        }
        finally
        {
            blocking.ReleaseRead();
            if (!demoted.ResourcesReleased)
            {
                try
                {
                    await demoted.DisposeAsync();
                }
                catch
                {
                    // ignored
                }
            }

            await successor.DisposeAsync();
        }
    }

    [Fact]
    public async Task Repeated_SeekStopDispose_Stress_DoesNotDeadlock()
    {
        var factory = new FakeAudioDecoderFactory((_, _) =>
            Task.FromResult<IAudioDecoder>(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromMilliseconds(300))));
        using var engine = new AudioEngine(
            factory,
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();

        for (var i = 0; i < 20; i++)
        {
            try
            {
                player.Seek(TimeSpan.FromMilliseconds(10 + (i % 5) * 20));
            }
            catch (InvalidOperationException)
            {
                // superseded / incomplete shutdown under stress
            }

            if (i % 4 == 0)
            {
                player.Stop();
                player.Play();
            }
        }

        player.Stop();
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    private static TrackDecodePipeline CreatePipeline(IAudioDecoder decoder)
    {
        var ring = new PcmRingBuffer(Math.Max(decoder.Format.ChannelCount * 256, 1024));
        return new TrackDecodePipeline("t.wav", decoder, ring, decoder.Duration);
    }

    private sealed class ThrowingReadDecoder : IAudioDecoder
    {
        public ThrowingReadDecoder(AudioFormat format) => Format = format;
        public AudioFormat Format { get; }
        public TimeSpan Duration => TimeSpan.FromSeconds(1);
        public bool HasKnownDuration => true;
        public bool CanSeek => true;
        public int Read(Span<float> destination) => throw new InvalidOperationException("decode-fail");
        public AudioSeekResult Seek(TimeSpan position) => AudioSeekResult.Exact(position, position);
        public void Dispose()
        {
        }
    }
}
