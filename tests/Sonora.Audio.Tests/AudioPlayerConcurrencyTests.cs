using Sonora.Audio.Formats;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public sealed class AudioPlayerConcurrencyTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 1);

    [Fact]
    public async Task InterleavedCommands_DoNotDeadlock()
    {
        var decoder = FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(2));
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            new FakeAudioOutputFactory(() => new FakeAudioOutput(blockSamples: 128)));
        using var player = engine.CreatePlayer();
        await player.LoadAsync("stress.wav");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var tasks = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        player.Play();
                        player.Pause();
                        player.Seek(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)));
                        player.Stop();
                        _ = player.Position;
                        _ = player.BufferedPosition;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        // ignored: transient state
                    }
                }
            }, cts.Token));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // expected when CTS cancels worker tokens
        }

        player.Stop();
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task RapidReload_DoesNotThrowUnobserved()
    {
        var format = Format;
        var factory = new FakeAudioDecoderFactory((_, _) =>
            Task.FromResult<Abstractions.IAudioDecoder>(FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(200))));
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory());
        using var player = engine.CreatePlayer();

        var loads = Enumerable.Range(0, 20)
            .Select(i => player.LoadAsync($"file{i}.wav"))
            .ToArray();

        await Task.WhenAll(loads);
        Assert.True(player.HasSource);
        player.Play();
        player.Stop();
    }
}
