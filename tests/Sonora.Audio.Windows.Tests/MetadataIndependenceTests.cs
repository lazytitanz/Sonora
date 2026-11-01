using System.Security.Cryptography;
using Sonora.Audio.Metadata;
using Sonora.Audio.Windows;

namespace Sonora.Audio.Windows.Tests;

public sealed class MetadataIndependenceTests
{
    [Fact]
    public void CreateMetadataReader_DoesNotRequireEngineOrWasapi()
    {
        var reader = SonoraWindows.CreateMetadataReader();
        Assert.IsType<AudioMetadataReader>(reader);
        // Constructing the reader must not throw and must not need an AudioEngine.
    }

    [Fact]
    public void Read_DoesNotAlterPlayerState_OrModifyFile()
    {
        var fixture = FixtureHelper.EnsureToneWav(TimeSpan.FromSeconds(1));
        var beforeHash = Sha256(fixture);

        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        var stateBefore = player.State;

        var reader = SonoraWindows.CreateMetadataReader();
        var meta = reader.Read(fixture);

        Assert.Equal(stateBefore, player.State);
        Assert.Null(player.SourcePath);
        Assert.NotNull(meta.Duration);
        Assert.Equal(beforeHash, Sha256(fixture));
    }

    [Fact]
    public void Read_ReleasesFileHandle()
    {
        var fixture = FixtureHelper.EnsureToneWav(TimeSpan.FromSeconds(1));
        var reader = new AudioMetadataReader();
        _ = reader.Read(fixture);

        using var exclusive = new FileStream(
            fixture,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.True(exclusive.CanRead);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
