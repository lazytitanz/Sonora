using Sonora.Audio.Windows.Decoding;

namespace Sonora.Audio.Windows.Tests;

public sealed class WindowsDecoderTests
{
    [Fact]
    public void Wav_DecodeSeekAndDuration()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(250));
        using var decoder = WindowsAudioDecoder.Open(path);

        Assert.True(decoder.Duration > TimeSpan.FromMilliseconds(200));
        Assert.True(decoder.CanSeek);
        Assert.Equal(2, decoder.Format.ChannelCount);

        var buffer = new float[4096];
        var total = 0;
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            total += read;
        }

        Assert.True(total > 1000);

        decoder.Seek(TimeSpan.FromMilliseconds(100));
        var afterSeek = decoder.Read(buffer);
        Assert.True(afterSeek > 0);
    }

    [Fact]
    public void Mp3_DecodeSeekAndDuration()
    {
        string path;
        try
        {
            path = FixtureHelper.EnsureToneMp3(TimeSpan.FromMilliseconds(300));
        }
        catch (Exception ex)
        {
            Assert.Fail($"MP3 fixture generation failed: {ex}");
            return;
        }

        using var decoder = WindowsAudioDecoder.Open(path);
        Assert.True(decoder.Duration > TimeSpan.FromMilliseconds(100));
        Assert.True(decoder.CanSeek);

        var buffer = new float[4096];
        Assert.True(decoder.Read(buffer) > 0);

        decoder.Seek(TimeSpan.FromMilliseconds(50));
        Assert.True(decoder.Read(buffer) > 0);
    }

    [Fact]
    public void M4a_DecodeSeekAndDuration()
    {
        string path;
        try
        {
            path = FixtureHelper.EnsureToneM4a(TimeSpan.FromMilliseconds(300));
        }
        catch (Exception ex)
        {
            Assert.Fail($"M4A fixture generation failed: {ex}");
            return;
        }

        using var decoder = WindowsAudioDecoder.Open(path);
        Assert.True(decoder.Duration > TimeSpan.FromMilliseconds(100), $"Duration was {decoder.Duration}.");
        Assert.True(decoder.CanSeek);

        var buffer = new float[4096];
        var first = decoder.Read(buffer);
        Assert.True(first > 0, "M4A decode must produce PCM samples.");

        decoder.Seek(TimeSpan.FromMilliseconds(50));
        Assert.True(decoder.Read(buffer) > 0, "M4A seek must leave the decoder readable.");
    }

    [Fact]
    public void MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            WindowsAudioDecoder.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav")));
    }
}
