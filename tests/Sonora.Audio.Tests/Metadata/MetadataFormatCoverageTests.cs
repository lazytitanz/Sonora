using Sonora.Audio.Metadata;

namespace Sonora.Audio.Tests.Metadata;

/// <summary>
/// Format-by-format verification. Claims support only when a real fixture succeeds.
/// Sources under Windows CommittedFixtures are copied and tagged when present.
/// </summary>
public sealed class MetadataFormatCoverageTests
{
    private readonly IAudioMetadataReader _reader = new AudioMetadataReader();

    public static TheoryData<string, string> FormatCases
    {
        get
        {
            var data = new TheoryData<string, string>
            {
                // Always available: generated WAV
                { "WAV", MetadataFixtureHelper.WavBasic },
            };

            foreach (var (label, fileName) in CandidateSources())
            {
                var source = ResolveCommitted(fileName);
                if (source is null)
                {
                    continue;
                }

                var tagged = MetadataFixtureHelper.PrepareTaggedCopy(
                    source,
                    $"tagged-{fileName}",
                    track =>
                    {
                        track.Title = $"Title-{label}";
                        track.Artist = $"Artist-{label}";
                        track.Album = $"Album-{label}";
                        track.Genre = "Test";
                        track.Year = 2026;
                        track.TrackNumber = 3;
                    });

                if (tagged is not null)
                {
                    data.Add(label, tagged);
                }
            }

            return data;
        }
    }

    public MetadataFormatCoverageTests()
    {
        MetadataFixtureHelper.EnsureAll();
    }

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void Format_BasicTags_Duration_AndTechProps(string label, string path)
    {
        var meta = _reader.Read(path);

        Assert.True(File.Exists(path), label);
        if (label == "WAV")
        {
            Assert.Equal("Sonora Basic", meta.Title);
            Assert.Equal(["Test Artist"], meta.Artists);
        }
        else
        {
            Assert.Equal($"Title-{label}", meta.Title);
            Assert.Equal([$"Artist-{label}"], meta.Artists);
            Assert.Equal($"Album-{label}", meta.Album);
            Assert.Equal(3, meta.TrackNumber);
            Assert.Equal(2026, meta.Year);
        }

        Assert.NotNull(meta.Duration);
        Assert.True(meta.Duration > TimeSpan.Zero, $"{label}: duration");

        // WebM Opus via ATL may omit sample rate / channel count; other formats should report them.
        if (label is not "WebMOpus")
        {
            Assert.NotNull(meta.SampleRate);
            Assert.True(meta.SampleRate > 0, $"{label}: sample rate");
            Assert.NotNull(meta.ChannelCount);
            Assert.True(meta.ChannelCount > 0, $"{label}: channels");
        }
    }

    [Fact]
    public void Format_Artwork_VerifiedOnWav()
    {
        var path = MetadataFixtureHelper.WavFrontBackArt;
        var without = _reader.Read(path);
        Assert.Empty(without.Artwork);

        var withArt = _reader.Read(path, new AudioMetadataReadOptions { IncludeArtwork = true });
        Assert.Equal(2, withArt.Artwork.Count);
        Assert.All(withArt.Artwork, a => Assert.True(a.Data.Length > 0));
    }

    [Fact]
    public void LosslessClassification_VerifiedFormats()
    {
        // ATL 7.19.0 reports FLAC as CF_LOSSLESS and MP3 as CF_LOSSY.
        // ALAC is reported as CF_LOSSY by ATL — Sonora returns that signal (documented limitation).
        AssertLossless("tone-1s.flac", expected: true);
        AssertLossless("tone-1s.mp3", expected: false);

        var alacSource = ResolveCommitted("tone-1s-alac.m4a");
        if (alacSource is not null)
        {
            var tagged = MetadataFixtureHelper.PrepareTaggedCopy(
                alacSource, "lossless-tone-1s-alac.m4a", t => t.Title = "ALAC");
            if (tagged is not null)
            {
                var meta = _reader.Read(tagged);
                // Record ATL behavior rather than inventing losslessness from the extension.
                Assert.False(meta.IsLossless);
            }
        }
    }

    private void AssertLossless(string fileName, bool expected)
    {
        var source = ResolveCommitted(fileName);
        if (source is null)
        {
            return;
        }

        var tagged = MetadataFixtureHelper.PrepareTaggedCopy(
            source, $"lossless-{fileName}", t => t.Title = fileName);
        if (tagged is null)
        {
            return;
        }

        Assert.Equal(expected, _reader.Read(tagged).IsLossless);
    }

    private static IEnumerable<(string Label, string FileName)> CandidateSources()
    {
        yield return ("MP3", "tone-1s.mp3");
        yield return ("FLAC", "tone-1s.flac");
        yield return ("OggVorbis", "tone-1s.ogg");
        yield return ("OggOpus", "tone-1s.opus");
        yield return ("AIFF", "tone-1s.aiff");
        yield return ("M4A_AAC", "tone-1s.m4a");
        yield return ("ALAC", "tone-1s-alac.m4a");
        yield return ("WMA", "tone-1s.wma");
        yield return ("WebMOpus", "tone-1s-opus.webm");
    }

    private static string? ResolveCommitted(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "CommittedFixtures", fileName),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "Sonora.Audio.Windows.Tests", "CommittedFixtures", fileName)),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
