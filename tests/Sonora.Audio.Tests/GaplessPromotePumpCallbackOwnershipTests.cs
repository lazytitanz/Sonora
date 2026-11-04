using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Post-promote decode-pump callback ownership: promote rebinds active-track progress/error
/// callbacks without restarting the preload worker.
/// </summary>
public sealed class GaplessPromotePumpCallbackOwnershipTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 1);

    [Fact]
    public async Task AfterNaturalPromote_SamplesDecoded_Advances()
    {
        // A short so promote is quick; B long so post-promote decode has room to grow.
        var aSamples = Enumerable.Repeat(0.2f, 200).ToArray();
        var bSamples = Enumerable.Repeat(0.5f, 40_000).ToArray();

        FakeAudioOutput? output = null;
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            var samples = path == "a.wav" ? aSamples : bSamples;
            return Task.FromResult<IAudioDecoder>(
                new SequenceFakeDecoder(Format, (float[])samples.Clone()));
        });
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

        var transitioned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.TrackTransitioned += (_, _) => transitioned.TrySetResult();

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("b.wav");
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();

        await transitioned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("b.wav", player.SourcePath);

        var atPromote = player.GetDiagnostics().SamplesDecoded;
        var deadline = Environment.TickCount64 + 2000;
        long after;
        do
        {
            await Task.Delay(50);
            after = player.GetDiagnostics().SamplesDecoded;
            if (after > atPromote)
            {
                break;
            }
        }
        while (Environment.TickCount64 < deadline);

        Assert.True(
            after > atPromote,
            $"SamplesDecoded should advance after promote callback rebind. atPromote={atPromote} after={after}");

        Assert.NotNull(output);
        Assert.Equal(1, output!.InitializeCount);
    }
}
