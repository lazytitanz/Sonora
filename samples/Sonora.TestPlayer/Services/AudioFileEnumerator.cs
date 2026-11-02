namespace Sonora.TestPlayer.Services;

/// <summary>
/// Finds local audio files under a directory for queue import.
/// </summary>
internal static class AudioFileEnumerator
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".wav",
        ".flac",
        ".ogg",
        ".m4a",
        ".aac",
        ".wma",
        ".aiff",
        ".aif",
        ".opus",
    };

    /// <summary>
    /// Returns full paths of audio files under <paramref name="directory"/>, optionally recursive,
    /// ordered by full path (ordinal ignore-case). Inaccessible subfolders are skipped.
    /// </summary>
    public static IReadOnlyList<string> Enumerate(string directory, bool recursive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        Collect(directory, recursive, results);
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>True when <paramref name="path"/> looks like a supported audio file by extension.</summary>
    public static bool IsAudioFile(string path) =>
        !string.IsNullOrWhiteSpace(path) && AudioExtensions.Contains(Path.GetExtension(path));

    private static void Collect(string directory, bool recursive, List<string> results)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (AudioExtensions.Contains(Path.GetExtension(file)))
                {
                    results.Add(file);
                }
            }

            if (!recursive)
            {
                return;
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                Collect(sub, recursive: true, results);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip folders the process cannot read.
        }
        catch (DirectoryNotFoundException)
        {
            // Race with deletion / remount.
        }
        catch (IOException)
        {
            // Skip transient IO failures for a subtree.
        }
    }
}
