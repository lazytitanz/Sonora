using Sonora.TestPlayer.Services;

namespace Sonora.TestPlayer.Tests;

public sealed class AudioFileEnumeratorTests
{
    [Fact]
    public void Enumerate_Recursive_FindsNestedAudioAndSkipsOtherFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "SonoraAudioEnum_" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "Album", "Disc 1");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(root, "root.mp3"), "x");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "x");
            File.WriteAllText(Path.Combine(nested, "track.flac"), "x");
            File.WriteAllText(Path.Combine(nested, "cover.jpg"), "x");

            var files = AudioFileEnumerator.Enumerate(root, recursive: true);

            Assert.Equal(2, files.Count);
            Assert.Contains(files, p => p.EndsWith("root.mp3", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, p => p.EndsWith("track.flac", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void Enumerate_NonRecursive_IgnoresSubfolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "SonoraAudioEnum_" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "Album");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(root, "root.wav"), "x");
            File.WriteAllText(Path.Combine(nested, "nested.mp3"), "x");

            var files = AudioFileEnumerator.Enumerate(root, recursive: false);

            Assert.Single(files);
            Assert.EndsWith("root.wav", files[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
