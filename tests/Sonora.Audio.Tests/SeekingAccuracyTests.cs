using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Seeking;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public sealed class SeekingAccuracyTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public void AudioFormat_TimeToFrames_UsesFloorTickArithmetic()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var frames = format.TimeToFrames(TimeSpan.FromMilliseconds(100));
        Assert.Equal(4410, frames);
        Assert.Equal(8820, format.TimeToSamples(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, format.TimeToSamples(TimeSpan.FromMilliseconds(100)) % format.ChannelCount);

        // Sub-frame time floors rather than rounding up.
        var tiny = TimeSpan.FromTicks(1);
        Assert.Equal(0, format.TimeToFrames(tiny));

        // FramesToTime round-trips through TimeToFrames for arbitrary frame indices.
        foreach (var f in new long[] { 1, 123, 4410, 44100, 123456 })
        {
            Assert.Equal(f, format.TimeToFrames(format.FramesToTime(f)));
        }
    }

    [Fact]
    public void AudioSeekResult_Unknown_HasNullActual()
    {
        var result = AudioSeekResult.Unknown(TimeSpan.FromSeconds(1)).EnsureInvariants();
        Assert.Equal(AudioSeekAccuracy.Unknown, result.Accuracy);
        Assert.Null(result.ActualPosition);
    }

    [Fact]
    public async Task SeekWithResult_Exact_ReportsRequestedAndActual()
    {
        var samples = CreateIndexCodedPcm(Format, frameCount: 44100);
        var decoder = new SequenceFakeDecoder(Format, samples);
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("index.wav");

        var target = TimeSpan.FromMilliseconds(250);
        var result = player.SeekWithResult(target);

        Assert.Equal(AudioSeekAccuracy.Exact, result.Accuracy);
        Assert.Equal(Format.FramesToTime(Format.TimeToFrames(target)), result.RequestedPosition);
        Assert.Equal(result.RequestedPosition, result.ActualPosition);
        Assert.Equal(result.ActualPosition, player.Position);
    }

    [Fact]
    public async Task SeekWithResult_Unknown_DoesNotFabricateActual()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(1));
        decoder.SeekAccuracy = AudioSeekAccuracy.Unknown;
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("u.wav");

        var result = player.SeekWithResult(TimeSpan.FromMilliseconds(400));
        Assert.Equal(AudioSeekAccuracy.Unknown, result.Accuracy);
        Assert.Null(result.ActualPosition);
        // Timeline intent still follows the request for UI continuity.
        Assert.Equal(result.RequestedPosition, player.Position);
    }

    [Fact]
    public async Task Seek_WhilePaused_RemainsPaused()
    {
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(hold),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("p.wav");
        player.Play();
        player.Pause();

        player.Seek(TimeSpan.FromMilliseconds(100));
        Assert.Equal(PlaybackState.Paused, player.State);
    }

    [Fact]
    public async Task Seek_WhilePlaying_ResumesPlaying()
    {
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)));
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(hold),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("play.wav");
        player.Play();

        player.Seek(TimeSpan.FromMilliseconds(100));
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [Fact]
    public async Task Seek_WithReadyNext_DoesNotPromoteOrComplete()
    {
        var factory = new FakeAudioDecoderFactory((_, _) =>
            Task.FromResult<IAudioDecoder>(
                new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2)))));
        using var engine = new AudioEngine(
            factory,
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        var transitions = 0;
        var completed = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        player.Play();
        var result = player.SeekWithResult(TimeSpan.FromMilliseconds(50));

        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        Assert.Equal("b.wav", player.NextTrackPath);
        Assert.Equal("a.wav", player.SourcePath);
        Assert.Equal(0, Volatile.Read(ref transitions));
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.True(result.RequestedPosition >= TimeSpan.Zero);
        player.Stop();
    }

    [Fact]
    public async Task SeekToZero_DuringOutputSwitch_PreservesZeroIntent()
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
        var decoder = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(3)));
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            outputs,
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero },
            devices);
        using var player = engine.CreatePlayer();
        await player.LoadAsync("track.wav");

        holdable.HoldRead();
        holdable.HoldShutdown();
        player.Play();
        Assert.True(holdable.WaitUntilReadHeld(TimeSpan.FromSeconds(2)));

        // Advance so a naive != Zero check would prefer the stale switch snapshot.
        player.Pause();
        player.Seek(TimeSpan.FromMilliseconds(500));
        Assert.True(player.Position >= TimeSpan.FromMilliseconds(400));

        holdable.HoldRead();
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

        player.Seek(TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, player.Position);

        holdable.ReleaseShutdown();
        await switchTask;

        // Must not restore the pre-switch ~500 ms snapshot; Playing may advance a few ms.
        Assert.True(
            player.Position < TimeSpan.FromMilliseconds(100),
            $"Seek-to-zero was overridden; Position={player.Position}.");
        Assert.Equal(OutputDeviceStatus.Available, player.OutputDeviceStatus);
    }

    [Fact]
    public async Task Seek_DoesNotRaisePlaybackCompleted()
    {
        var samples = CreateIndexCodedPcm(Format, frameCount: 4410);
        var decoder = new SequenceFakeDecoder(Format, samples);
        using var engine = CreateEngine(decoder);
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("short.wav");
        player.Play();
        // Drain toward EOS, then seek back — must not complete from stale buffers.
        var spun = 0;
        while (player.BufferedPosition < player.Duration - TimeSpan.FromMilliseconds(20) && spun++ < 500)
        {
            await Task.Yield();
        }

        player.Seek(TimeSpan.Zero);
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.Equal(PlaybackState.Playing, player.State);
        player.Stop();
        Assert.Equal(0, Volatile.Read(ref completed));
    }

    [Fact]
    public void SequenceFakeDecoder_Seek_LandsOnExactSampleIndex()
    {
        var samples = CreateIndexCodedPcm(Format, frameCount: 1000);
        using var decoder = new SequenceFakeDecoder(Format, samples);
        var expectedFrames = 123L;
        var result = decoder.Seek(Format.FramesToTime(expectedFrames));
        Assert.Equal(AudioSeekAccuracy.Exact, result.Accuracy);
        Assert.Equal(expectedFrames * Format.ChannelCount, decoder.PositionSamples);

        var buffer = new float[Format.ChannelCount];
        Assert.Equal(Format.ChannelCount, decoder.Read(buffer));
        Assert.Equal(expectedFrames, (long)buffer[0]);
    }

    private static float[] CreateIndexCodedPcm(AudioFormat format, int frameCount)
    {
        var samples = new float[frameCount * format.ChannelCount];
        for (var f = 0; f < frameCount; f++)
        {
            for (var ch = 0; ch < format.ChannelCount; ch++)
            {
                samples[f * format.ChannelCount + ch] = f;
            }
        }

        return samples;
    }

    private static AudioEngine CreateEngine(IAudioDecoder decoder) =>
        new(
            new FakeAudioDecoderFactory(decoder),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });

    private static FakeAudioOutputDevices TwoDevices() =>
        new(
            new[]
            {
                new AudioOutputDevice("a", "Speakers", true, AudioDeviceState.Active),
                new AudioOutputDevice("b", "Headphones", false, AudioDeviceState.Active),
            },
            "a");
}
