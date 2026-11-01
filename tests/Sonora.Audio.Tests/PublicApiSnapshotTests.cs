using System.Runtime.CompilerServices;
using Sonora.Audio.Tests.PublicApi;

namespace Sonora.Audio.Tests;

/// <summary>
/// Locks the Sonora.Audio public API for the v1.0 RC baseline (after additive DisposeAsync).
/// Set SONORA_UPDATE_PUBLIC_API_BASELINE=1 to regenerate Baselines/Sonora.Audio.PublicApi.txt.
/// </summary>
public sealed class PublicApiSnapshotTests
{
    [Fact]
    public void SonoraAudio_PublicApi_MatchesBaseline()
    {
        var actual = PublicApiDumper.Dump(typeof(AudioEngine).Assembly);
        var baselinePath = GetBaselinePath("Sonora.Audio.PublicApi.txt");

        if (ShouldUpdateBaseline())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, actual);
            // Also refresh the v0.9 annotated copy (same surface; DisposeAsync is the additive freeze delta).
            var v09 = "# v0.9 API freeze snapshot (same surface as v1.0 RC).\n"
                      + "# DisposeAsync on AudioEngine/AudioPlayer is the intentional additive change from the freeze.\n"
                      + actual;
            File.WriteAllText(GetBaselinePath("Sonora.Audio.PublicApi.v0.9.txt"), v09);
        }

        Assert.True(File.Exists(baselinePath), $"Missing baseline at {baselinePath}. Run with SONORA_UPDATE_PUBLIC_API_BASELINE=1.");
        var expected = File.ReadAllText(baselinePath).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    private static bool ShouldUpdateBaseline()
        => string.Equals(Environment.GetEnvironmentVariable("SONORA_UPDATE_PUBLIC_API_BASELINE"), "1", StringComparison.Ordinal);

    private static string GetBaselinePath(string fileName, [CallerFilePath] string sourceFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(sourceFile)!;
        return Path.Combine(testProjectDir, "Baselines", fileName);
    }
}
