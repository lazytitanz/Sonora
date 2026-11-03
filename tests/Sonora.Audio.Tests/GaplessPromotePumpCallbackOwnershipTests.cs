using Sonora.Audio.Abstractions;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Characterization of the known post-promote decode-pump callback ownership defect (C5).
/// Independent of the seam PCM concatenation theory and of WASAPI session rebuilding.
/// See <c>docs/gapless-callback-ownership.md</c>.
/// </summary>
public sealed class GaplessPromotePumpCallbackOwnershipTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 1);

    /// <summary>
    /// After a verified natural promote, <see cref="PlaybackDiagnostics.SamplesDecoded"/> should
    /// advance while B continues to decode. Today the preload pump keeps the no-op
    /// <c>onSamplesWritten</c> installed at PreloadNextAsync, so the counter stays flat.
    /// Flip the final assertion when promote rebinds active-track pump callbacks.
    /// </summary>
    [Fact]
    public async Task Characterization_AfterNaturalPromote_SamplesDecoded_DoesNotAdvance_UntilCallbackRebindFixed()
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
        await Task.Delay(200);
        var after = player.GetDiagnostics().SamplesDecoded;

        // Characterization of the current defect: counter does not reflect B's active decode.
        // When fixed, expect: after > atPromote (and delete/replace this characterization).
        Assert.True(
            after == atPromote,
            "Unexpected: SamplesDecoded advanced after promote. If pump callbacks were rebound, "
            + "replace this characterization with a positive contract test. "
            + $"atPromote={atPromote} after={after}");

        Assert.NotNull(output);
        Assert.Equal(1, output!.InitializeCount);
    }
}
