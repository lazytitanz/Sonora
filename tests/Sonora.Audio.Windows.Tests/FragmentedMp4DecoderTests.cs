using Sonora.Audio;
using Sonora.Audio.Windows.Decoding;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Fragmented MPEG-4 / ManagedCodecs AAC path and empty-PCM failure semantics.
/// </summary>
public sealed class FragmentedMp4DecoderTests
{
    [Fact]
    public void CommittedFmp4Fixture_SniffsAsFragmented()
    {
        var path = CommittedFmp4Path();
        Assert.Equal(AudioContainerKind.FragmentedMpeg4, DecoderSelector.Sniff(path));
    }

    [Fact]
    public void CommittedFmp4Fixture_DecodesSeekAndDuration()
    {
        var path = CommittedFmp4Path();
        using var decoder = WindowsAudioDecoder.Open(path);

        Assert.Equal(44100, decoder.Format.SampleRate);
        Assert.Equal(2, decoder.Format.ChannelCount);
        Assert.True(decoder.HasKnownDuration);
        Assert.True(decoder.Duration > TimeSpan.FromMilliseconds(500), $"Duration was {decoder.Duration}.");
        Assert.True(decoder.CanSeek);

        var buffer = new float[8192];
        var first = decoder.Read(buffer);
        Assert.True(first > 0, "fMP4 must produce PCM on first read.");

        long total = first;
        int n;
        while ((n = decoder.Read(buffer)) > 0)
        {
            total += n;
        }

        Assert.True(total > decoder.Format.SampleRate, $"Decoded too few samples ({total}).");

        decoder.Seek(TimeSpan.FromMilliseconds(200));
        Assert.True(decoder.Read(buffer) > 0, "fMP4 must remain readable after seek.");
    }

    [Fact]
    public void ProgressiveM4a_StillRoutesThroughMediaFoundationHappyPath()
    {
        var path = FixtureHelper.EnsureToneM4a(TimeSpan.FromMilliseconds(300));
        Assert.Equal(AudioContainerKind.Mpeg4, DecoderSelector.Sniff(path));

        using var decoder = WindowsAudioDecoder.Open(path);
        var buffer = new float[4096];
        Assert.True(decoder.Read(buffer) > 0);
    }

    [Fact]
    public void FtypDashBrand_SniffsAsFragmentedWithoutMoofScan()
    {
        // size(4) + ftyp + major(dash) + minor + compat
        var bytes = new byte[]
        {
            0, 0, 0, 24,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'d', (byte)'a', (byte)'s', (byte)'h',
            0, 0, 0, 0,
            (byte)'i', (byte)'s', (byte)'o', (byte)'6',
            (byte)'m', (byte)'p', (byte)'4', (byte)'1',
        };
        var path = Path.Combine(FixtureHelper.FixturesDirectory, "sniff-dash-ftyp.bin");
        File.WriteAllBytes(path, bytes);
        Assert.Equal(AudioContainerKind.FragmentedMpeg4, DecoderSelector.Sniff(path));
    }

    [Fact]
    public void LocalEmpFmp4_DecodesWhenPresent()
    {
        var path = Environment.GetEnvironmentVariable("SONORA_FMP4_REGRESSION_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = @"C:\Users\evanl\Music\D.I.T.C\D.I.T.C\11 Da Enemy.m4a";
        }

        if (!File.Exists(path))
        {
            return; // optional local regression; CI uses committed synthetic fixture
        }

        Assert.Equal(AudioContainerKind.FragmentedMpeg4, DecoderSelector.Sniff(path));
        using var decoder = WindowsAudioDecoder.Open(path);
        var buffer = new float[8192];
        Assert.True(decoder.Read(buffer) > 0);
        Assert.True(decoder.Duration > TimeSpan.FromMinutes(2));
        decoder.Seek(TimeSpan.FromSeconds(30));
        Assert.True(decoder.Read(buffer) > 0);
    }

    private static string CommittedFmp4Path()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CommittedFixtures", "tone-fmp4-1s.m4a");
        Assert.True(File.Exists(path), $"Missing committed fixture at {path}. Run scripts/generate-fmp4-fixture.");
        return path;
    }
}
