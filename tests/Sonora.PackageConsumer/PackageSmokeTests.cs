using Sonora.Audio;
using Sonora.Audio.Playback;
using Xunit;

namespace Sonora.PackageConsumer;

/// <summary>
/// Minimal restore/load smoke for packed NuGet packages from a local artifacts feed.
/// </summary>
public sealed class PackageSmokeTests
{
    [Fact]
    public void SonoraAudio_PublicTypes_AreLoadable()
    {
        var engineType = typeof(AudioEngine);
        Assert.Equal("Sonora.Audio", engineType.Assembly.GetName().Name);
        Assert.NotNull(typeof(AudioPlayer));
        Assert.NotNull(typeof(AudioEngineOptions));
    }

    [Fact]
    [Trait("Category", "WindowsPackage")]
    public void SonoraAudioWindows_Factory_IsLoadable()
    {
#if SONORA_WINDOWS_PACKAGE
        var factory = typeof(global::Sonora.Audio.Windows.SonoraWindows);
        Assert.Equal("Sonora.Audio.Windows", factory.Assembly.GetName().Name);
        Assert.True(factory.GetMethod("CreateEngine") is not null);
#else
        // Linux / SonoraPackageConsumerWindows=false: Windows package is not restored.
        Assert.True(true);
#endif
    }
}
