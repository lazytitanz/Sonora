using Sonora.Audio;
using Sonora.Audio.Windows;
using Sonora.Audio.Windows.Decoding;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Decode / seek / duration gates for formats added in 0.2.0.
/// </summary>
public sealed class CodecExpansionTests
{
    [Theory]
    [InlineData("aiff")]
    [InlineData("aac")]
    [InlineData("wma")]
    [InlineData("alac")]
    [InlineData("ogg")]
    [InlineData("opus")]
    [InlineData("webm-opus")]
    [InlineData("webm-vorbis")]
    public void Format_SniffDecodeSeekAndDuration(string formatKey)
    {
        var path = ResolveFixture(formatKey);
        var kind = DecoderSelector.Sniff(path);
        Assert.Equal(ExpectedKind(formatKey), kind);

        using var decoder = WindowsAudioDecoder.Open(path);
        Assert.True(decoder.CanSeek, $"{formatKey} should report seek support.");
        Assert.True(decoder.Duration > TimeSpan.FromMilliseconds(400), $"{formatKey} duration was {decoder.Duration}.");
        Assert.True(decoder.Format.ChannelCount >= 1);

        var buffer = new float[4096];
        var first = decoder.Read(buffer);
        Assert.True(first > 0, $"{formatKey} must decode PCM.");

        decoder.Seek(TimeSpan.FromMilliseconds(200));
        var afterSeek = decoder.Read(buffer);
        Assert.True(afterSeek > 0, $"{formatKey} must remain readable after seek.");
    }

    [Theory]
    [InlineData("aiff")]
    [InlineData("aac")]
    [InlineData("wma")]
    [InlineData("alac")]
    [InlineData("ogg")]
    [InlineData("opus")]
    [InlineData("webm-opus")]
    public async Task Format_NaturalCompletion(string formatKey)
    {
        WindowsAudioEnvironment.SkipIfNoWasapiOutput();
        var path = ResolveFixture(formatKey);
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();

        await player.LoadAsync(path);
        using var completed = new ManualResetEventSlim(false);
        player.PlaybackCompleted += (_, _) => completed.Set();

        player.Play();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), $"{formatKey} did not complete naturally.");
        Assert.Equal(Sonora.Audio.Formats.PlaybackState.Stopped, player.State);
    }

    [Fact]
    public void UnsupportedOggWithoutCodec_IsRejectedOrRouted()
    {
        // Empty OggS without OpusHead/vorbis should sniff as Unknown.
        var path = Path.Combine(FixtureHelper.FixturesDirectory, "bogus.ogg");
        File.WriteAllBytes(path, "OggS"u8.ToArray().Concat(new byte[28]).ToArray());
        Assert.Equal(AudioContainerKind.Unknown, DecoderSelector.Sniff(path));
        Assert.ThrowsAny<AudioException>(() => WindowsAudioDecoder.Open(path));
    }

    private static string ResolveFixture(string formatKey) => formatKey switch
    {
        "aiff" => FixtureHelper.EnsureToneAiff(TimeSpan.FromMilliseconds(1000)),
        "aac" => FixtureHelper.EnsureToneAdtsAac(TimeSpan.FromMilliseconds(1000)),
        "wma" => FixtureHelper.EnsureToneWma(TimeSpan.FromMilliseconds(1000)),
        "alac" => FixtureHelper.EnsureToneAlacM4a(),
        "ogg" => FixtureHelper.EnsureToneOggVorbis(),
        "opus" => FixtureHelper.EnsureToneOpus(),
        "webm-opus" => FixtureHelper.EnsureToneWebmOpus(),
        "webm-vorbis" => FixtureHelper.EnsureToneWebmVorbis(),
        _ => throw new ArgumentOutOfRangeException(nameof(formatKey)),
    };

    private static AudioContainerKind ExpectedKind(string formatKey) => formatKey switch
    {
        "aiff" => AudioContainerKind.Aiff,
        "aac" => AudioContainerKind.AdtsAac,
        "wma" => AudioContainerKind.Asf,
        "alac" => AudioContainerKind.Mpeg4,
        "ogg" => AudioContainerKind.OggVorbis,
        "opus" => AudioContainerKind.OggOpus,
        "webm-opus" => AudioContainerKind.WebM,
        "webm-vorbis" => AudioContainerKind.WebM,
        _ => throw new ArgumentOutOfRangeException(nameof(formatKey)),
    };
}
