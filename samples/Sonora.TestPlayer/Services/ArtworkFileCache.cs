using System.Security.Cryptography;

namespace Sonora.TestPlayer.Services;

/// <summary>
/// Writes embedded artwork bytes into the Vetra web root so the UI can load them with a short
/// relative URL instead of a multi-hundred-kilobyte <c>data:</c> URL over the bridge.
/// </summary>
public sealed class ArtworkFileCache
{
    private readonly string _directory;

    /// <summary>Creates a cache that writes under <paramref name="directory"/> (created if missing).</summary>
    public ArtworkFileCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// Persists image bytes when needed and returns a web-root-relative URL such as
    /// <c>_artwork/ab12….jpg</c>. Returns <see langword="null"/> for empty or unrecognized data.
    /// </summary>
    public string? Store(ReadOnlySpan<byte> data)
    {
        var ext = ExtensionFor(data);
        if (ext is null || data.IsEmpty)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var fileName = string.Concat(hash.AsSpan(0, 32), ext);
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path))
        {
            // Write via a temp file so a concurrent <img> load never sees a partial image.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, data.ToArray());
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another writer finished first.
            }
            finally
            {
                TryDelete(temp);
            }
        }

        // Root-relative so resolution does not depend on the current page path.
        return "/_artwork/" + fileName;
    }

    private static string? ExtensionFor(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            return ".jpg";
        }

        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
        {
            return ".png";
        }

        if (d.Length >= 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'8')
        {
            return ".gif";
        }

        if (d.Length >= 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F'
            && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P')
        {
            return ".webp";
        }

        if (d.Length >= 2 && d[0] == (byte)'B' && d[1] == (byte)'M')
        {
            return ".bmp";
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
