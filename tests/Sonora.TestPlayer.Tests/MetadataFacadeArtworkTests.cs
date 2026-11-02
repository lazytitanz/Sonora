using Sonora.TestPlayer.Services;

namespace Sonora.TestPlayer.Tests;

public sealed class MetadataFacadeArtworkTests
{
    [Fact]
    public void BuildUiThumbnailDataUrl_ProducesCompactJpegDataUrl()
    {
        // 2x2 red JPEG (minimal valid JFIF-ish payload generated via System.Drawing in the method under test
        // needs a real decodeable image; craft one here).
        using var bitmap = new System.Drawing.Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.DarkRed);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        var bytes = ms.ToArray();

        var url = MetadataFacade.BuildUiThumbnailDataUrl(bytes);

        Assert.NotNull(url);
        Assert.StartsWith("data:image/jpeg;base64,", url, StringComparison.Ordinal);
        Assert.True(url.Length < 80_000, $"Thumbnail data URL too large: {url.Length}");
    }

    [Fact]
    public void BuildUiThumbnailDataUrl_ReturnsNull_ForGarbage()
    {
        Assert.Null(MetadataFacade.BuildUiThumbnailDataUrl([0x00, 0x01, 0x02]));
        Assert.Null(MetadataFacade.BuildUiThumbnailDataUrl(ReadOnlySpan<byte>.Empty));
    }
}
