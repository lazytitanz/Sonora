using Sonora.TestPlayer.Services;

namespace Sonora.TestPlayer.Tests;

public sealed class ArtworkFileCacheTests
{
    [Fact]
    public void Store_WritesJpeg_AndReturnsRelativeUrl()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonora-artwork-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ArtworkFileCache(dir);
            // Minimal JPEG: SOI + APP0-ish bytes ending with EOI is enough for the sniffer.
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9, 0x01, 0x02, 0x03, 0x04 };

            var url = cache.Store(jpeg);

            Assert.NotNull(url);
            Assert.StartsWith("/_artwork/", url, StringComparison.Ordinal);
            Assert.EndsWith(".jpg", url, StringComparison.OrdinalIgnoreCase);
            var fileName = url["/_artwork/".Length..];
            Assert.True(File.Exists(Path.Combine(dir, fileName)));

            // Second store of the same bytes reuses the file and URL.
            Assert.Equal(url, cache.Store(jpeg));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Store_ReturnsNull_ForUnrecognizedBytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonora-artwork-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ArtworkFileCache(dir);
            Assert.Null(cache.Store([0x00, 0x01, 0x02]));
            Assert.Null(cache.Store(ReadOnlySpan<byte>.Empty));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
