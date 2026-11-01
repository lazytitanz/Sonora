using Sonora.Audio.Tests.PublicApi;

namespace Sonora.Audio.Tests;

/// <summary>
/// Locks the Sonora.Audio public API for the v1.0 RC baseline (after additive DisposeAsync).
/// Set SONORA_UPDATE_PUBLIC_API_BASELINE=1 to regenerate Baselines under the test project
/// (and refresh the output copy when rebuilding).
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
            WriteBaselineUpdate(actual);
            baselinePath = GetBaselinePath("Sonora.Audio.PublicApi.txt");
        }

        Assert.True(
            File.Exists(baselinePath),
            $"Missing baseline at {baselinePath}. Ensure Baselines are CopyToOutputDirectory, or run with SONORA_UPDATE_PUBLIC_API_BASELINE=1.");
        var expected = File.ReadAllText(baselinePath).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    private static bool ShouldUpdateBaseline()
        => string.Equals(Environment.GetEnvironmentVariable("SONORA_UPDATE_PUBLIC_API_BASELINE"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Baseline files are copied next to the test assembly. Do not use <c>CallerFilePath</c>:
    /// deterministic CI builds / SourceLink rewrite it to mapped paths such as <c>/_/tests/...</c>.
    /// </summary>
    private static string GetBaselinePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Baselines", fileName);

    private static void WriteBaselineUpdate(string actual)
    {
        // Prefer writing next to the project sources when the repo layout is available;
        // fall back to the output directory so update mode still works under odd paths.
        var projectBaselines = FindProjectBaselinesDirectory();
        var targetDir = projectBaselines ?? Path.Combine(AppContext.BaseDirectory, "Baselines");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "Sonora.Audio.PublicApi.txt"), actual);
        var v09 = "# v0.9 API freeze snapshot (same surface as v1.0 RC).\n"
                  + "# DisposeAsync on AudioEngine/AudioPlayer is the intentional additive change from the freeze.\n"
                  + actual;
        File.WriteAllText(Path.Combine(targetDir, "Sonora.Audio.PublicApi.v0.9.txt"), v09);
    }

    private static string? FindProjectBaselinesDirectory()
    {
        // bin/Release/net10.0 → climb to the test project folder containing Baselines/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Baselines");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "Sonora.Audio.PublicApi.txt")))
            {
                return candidate;
            }

            var nested = Path.Combine(dir.FullName, "tests", "Sonora.Audio.Tests", "Baselines");
            if (Directory.Exists(nested))
            {
                return nested;
            }
        }

        return null;
    }
}
