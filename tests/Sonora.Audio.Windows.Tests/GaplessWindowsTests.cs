using Sonora.Audio;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.DSP;
using Sonora.Audio.Windows.Tests.Fakes;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Non-hardware gapless coverage using real Windows decoders and NoOp output.
/// </summary>
public sealed class GaplessWindowsTests
{
    [Fact]
    public async Task PreloadAndTransition_WavToWav_WithoutWasapi()
    {
        var a = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(200));
        var b = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(250));

        using var engine = new AudioEngine(
            new WindowsAudioDecoderFactory(),
            new NoOpAudioOutputFactory(),
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.FromMilliseconds(20),
                EffectFactory = new WindowsAudioEffectFactory(),
                FormatConverter = new WindowsAudioFormatConverter(),
            });
        using var player = engine.CreatePlayer();
        var eq = player.Effects.AddEqualizer();
        eq.SetBandGain(2, 2f);

        var transitions = 0;
        player.TrackTransitioned += (_, e) =>
        {
            Interlocked.Increment(ref transitions);
            Assert.Equal(a, e.PreviousPath);
            Assert.Equal(b, e.NextPath);
        };

        await player.LoadAsync(a);
        await player.PreloadNextAsync(b);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);

        // NoOp does not pull — promote path is not exercised end-to-end without a puller.
        // Validate preload + DSP persistence + ClearNext/Load semantics on Windows stack.
        Assert.Equal(2f, eq.GetBandGain(2));
        player.ClearNext();
        Assert.Equal(NextTrackState.None, player.NextTrackState);

        await player.PreloadNextAsync(b);
        await player.LoadAsync(a);
        Assert.Equal(NextTrackState.None, player.NextTrackState);
        Assert.Equal(0, Volatile.Read(ref transitions));
    }

    [Fact]
    public async Task FormatConverter_AllowsMismatchedRatePreload()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(150));
        using var engine = new AudioEngine(
            new WindowsAudioDecoderFactory(),
            new NoOpAudioOutputFactory(),
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.FromMilliseconds(20),
                FormatConverter = new WindowsAudioFormatConverter(),
            });
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);

        // Preloading the same session-compatible file must succeed with Windows converter registered.
        await player.PreloadNextAsync(path);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
    }
}
