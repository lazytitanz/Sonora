using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// End-to-end regression for pause/resume/seek transport smoothing through AudioPlayer + FakeAudioOutput.
/// </summary>
public sealed class TransportSmoothingPlaybackTests
{
    private static readonly AudioFormat Mono = AudioFormat.CreateFloat32(44100, 1);
    private const float Amplitude = 0.5f;
    private const float MaxAllowedAdjacentJump = 0.05f;

    [Fact]
    public async Task Pause_FadesToSilence_BeforePullStops()
    {
        await using var session = await CreatePlayingAsync(Amplitude);
        var output = session.Output;

        await WaitForAudibleAsync(output, Amplitude);

        var mark = CapturedCount(output);
        session.Player.Pause();
        Assert.Equal(PlaybackState.Paused, session.Player.State);

        float[] captured;
        lock (output.CapturedSamples)
        {
            captured = output.CapturedSamples.ToArray();
        }

        Assert.True(captured.Length > mark);
        var edge = captured.AsSpan(Math.Max(0, mark - 1));
        Assert.True(
            MaxAdjacentJump(edge) < MaxAllowedAdjacentJump,
            $"Pause edge jump {MaxAdjacentJump(edge):G6}");
        Assert.True(Math.Abs(edge[^1]) < MaxAllowedAdjacentJump, "Pause must end near silence.");
    }

    [Fact]
    public async Task Resume_FadesInFromSilence()
    {
        await using var session = await CreatePlayingAsync(Amplitude);
        var output = session.Output;

        await WaitForAudibleAsync(output, Amplitude);
        session.Player.Pause();
        await WaitUntilIdleAsync(output);

        var mark = CapturedCount(output);
        session.Player.Play();
        Assert.Equal(PlaybackState.Playing, session.Player.State);
        await WaitForAudibleAfterAsync(output, Amplitude, mark);

        float[] after;
        lock (output.CapturedSamples)
        {
            after = output.CapturedSamples.Skip(mark).ToArray();
        }

        Assert.NotEmpty(after);
        Assert.True(Math.Abs(after[0]) < MaxAllowedAdjacentJump, $"Resume onset {after[0]:G6}");
        Assert.True(
            MaxAdjacentJump(after) < MaxAllowedAdjacentJump,
            $"Resume jump {MaxAdjacentJump(after):G6}");
    }

    [Fact]
    public async Task SeekWhilePlaying_DoesNotExposePreSeekAmplitude()
    {
        var pre = Enumerable.Repeat(0.6f, 20_000).ToArray();
        var post = Enumerable.Repeat(-0.4f, 20_000).ToArray();
        var all = pre.Concat(post).ToArray();
        var seekAt = Mono.SamplesToTime(pre.Length);

        FakeAudioOutput? output = null;
        var factory = new FakeAudioDecoderFactory((_, _) =>
            Task.FromResult<IAudioDecoder>(new SequenceFakeDecoder(Mono, (float[])all.Clone())));
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 128, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        using var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                BufferDuration = TimeSpan.FromMilliseconds(250),
            });
        using var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        await player.LoadAsync("seek.wav");
        player.Play();
        await WaitForAudibleAsync(output!, 0.6f);

        player.Seek(seekAt);
        // Mark after Seek returns — fade-out of pre-seek PCM happens during Seek, before commit.
        var mark = CapturedCount(output!);
        await WaitForSampleNearAfterAsync(output!, -0.4f, mark);

        float[] after;
        lock (output!.CapturedSamples)
        {
            after = output.CapturedSamples.Skip(mark).ToArray();
        }

        var stale = after.Count(s => s > 0.3f);
        Assert.True(stale == 0, $"Stale pre-seek samples after seek commit: {stale}");
        Assert.True(MaxAdjacentJump(after) < 0.15f, $"Seek edge jump {MaxAdjacentJump(after):G6}");
    }

    [Fact]
    public async Task SeekWhilePaused_ThenResume_FadesIn()
    {
        await using var session = await CreatePlayingAsync(Amplitude);
        var output = session.Output;

        await WaitForAudibleAsync(output, Amplitude);
        session.Player.Pause();
        await WaitUntilIdleAsync(output);

        session.Player.Seek(TimeSpan.FromMilliseconds(50));
        Assert.Equal(PlaybackState.Paused, session.Player.State);

        var mark = CapturedCount(output);
        session.Player.Play();
        await WaitForAudibleAfterAsync(output, Amplitude, mark);

        float[] after;
        lock (output.CapturedSamples)
        {
            after = output.CapturedSamples.Skip(mark).ToArray();
        }

        Assert.NotEmpty(after);
        Assert.True(Math.Abs(after[0]) < MaxAllowedAdjacentJump);
        Assert.True(MaxAdjacentJump(after) < MaxAllowedAdjacentJump);
    }

    [Fact]
    public async Task RapidPausePlay_DoesNotStickMuted()
    {
        await using var session = await CreatePlayingAsync(Amplitude);
        var output = session.Output;
        var player = session.Player;

        await WaitForAudibleAsync(output, Amplitude);

        for (var i = 0; i < 10; i++)
        {
            player.Pause();
            player.Play();
        }

        Assert.Equal(PlaybackState.Playing, player.State);
        var mark = CapturedCount(output);
        await WaitForAdditionalAsync(output, mark + 512);
        lock (output.CapturedSamples)
        {
            var tail = output.CapturedSamples.TakeLast(256).ToArray();
            Assert.Contains(tail, s => Math.Abs(s - Amplitude) < 0.05f);
        }
    }

    [Fact]
    public async Task Pause_PreservesAccuratePosition()
    {
        await using var session = await CreatePlayingAsync(Amplitude);

        await WaitForAudibleAsync(session.Output, Amplitude);
        await Task.Delay(40);
        var before = session.Player.Position;
        session.Player.Pause();
        var after = session.Player.Position;

        Assert.True(after >= before - TimeSpan.FromMilliseconds(5));
        Assert.True(
            after - before < TimeSpan.FromMilliseconds(80),
            $"Position jumped too far across pause: before={before} after={after}");
        Assert.Equal(after, session.Player.Position);
    }

    private static async Task<PlayerSession> CreatePlayingAsync(float amplitude)
    {
        var samples = Enumerable.Repeat(amplitude, 88_200).ToArray();
        FakeAudioOutput? output = null;
        var factory = new FakeAudioDecoderFactory((_, _) =>
            Task.FromResult<IAudioDecoder>(new SequenceFakeDecoder(Mono, (float[])samples.Clone())));
        var outputs = new FakeAudioOutputFactory(() =>
        {
            output = new FakeAudioOutput(blockSamples: 128, latency: TimeSpan.FromMilliseconds(1));
            return output;
        });

        var engine = new AudioEngine(
            factory,
            outputs,
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.Zero,
                BufferDuration = TimeSpan.FromMilliseconds(250),
            });
        var player = engine.CreatePlayer();
        player.Effects.Bypass = true;
        await player.LoadAsync("tone.wav");
        player.Play();
        return new PlayerSession(engine, player, output!);
    }

    private static int CapturedCount(FakeAudioOutput output)
    {
        lock (output.CapturedSamples)
        {
            return output.CapturedSamples.Count;
        }
    }

    private static async Task WaitForAudibleAsync(FakeAudioOutput output, float amplitude)
        => await WaitForAudibleAfterAsync(output, amplitude, afterIndex: 0);

    private static async Task WaitForAudibleAfterAsync(FakeAudioOutput output, float amplitude, int afterIndex)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            lock (output.CapturedSamples)
            {
                for (var i = afterIndex; i < output.CapturedSamples.Count; i++)
                {
                    if (Math.Abs(output.CapturedSamples[i] - amplitude) < 0.05f)
                    {
                        return;
                    }
                }
            }

            await Task.Delay(5);
        }

        Assert.Fail("Timed out waiting for audible samples.");
    }

    private static async Task WaitForSampleNearAfterAsync(FakeAudioOutput output, float value, int afterIndex)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            lock (output.CapturedSamples)
            {
                for (var i = afterIndex; i < output.CapturedSamples.Count; i++)
                {
                    if (Math.Abs(output.CapturedSamples[i] - value) < 0.08f)
                    {
                        return;
                    }
                }
            }

            await Task.Delay(5);
        }

        Assert.Fail($"Timed out waiting for sample near {value}.");
    }

    private static async Task WaitForAdditionalAsync(FakeAudioOutput output, int minCount)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (CapturedCount(output) >= minCount)
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("Timed out waiting for additional captured samples.");
    }

    private static async Task WaitUntilIdleAsync(FakeAudioOutput output)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            if (output.IsPullLoopIdle)
            {
                return;
            }

            await Task.Delay(5);
        }
    }

    private static float MaxAdjacentJump(ReadOnlySpan<float> samples)
    {
        var max = 0f;
        for (var i = 1; i < samples.Length; i++)
        {
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));
        }

        return max;
    }

    private static float MaxAdjacentJump(IReadOnlyList<float> samples)
    {
        var max = 0f;
        for (var i = 1; i < samples.Count; i++)
        {
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));
        }

        return max;
    }

    private sealed class PlayerSession : IAsyncDisposable
    {
        private readonly AudioEngine _engine;

        public PlayerSession(AudioEngine engine, AudioPlayer player, FakeAudioOutput output)
        {
            _engine = engine;
            Player = player;
            Output = output;
        }

        public AudioPlayer Player { get; }
        public FakeAudioOutput Output { get; }

        public async ValueTask DisposeAsync()
        {
            Player.Dispose();
            await _engine.DisposeAsync().ConfigureAwait(false);
        }
    }
}
