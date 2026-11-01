using System.Collections;
using Sonora.Audio.Metadata;

namespace Sonora.Audio.Tests.Metadata;

public sealed class AudioMetadataReaderTests
{
    private readonly IAudioMetadataReader _reader = new AudioMetadataReader();

    public AudioMetadataReaderTests()
    {
        MetadataFixtureHelper.EnsureAll();
    }

    [Fact]
    public void Read_BasicTags_MapsNormalizedFields()
    {
        var meta = _reader.Read(MetadataFixtureHelper.WavBasic);

        Assert.Equal("Sonora Basic", meta.Title);
        Assert.Equal(["Test Artist"], meta.Artists);
        Assert.Equal("Test Album", meta.Album);
        Assert.Equal(["Album Artist"], meta.AlbumArtists);
        Assert.Equal("Electronic", meta.Genre);
        Assert.Equal(["Electronic"], meta.Genres);
        Assert.Equal(2, meta.TrackNumber);
        Assert.Equal(10, meta.TrackCount);
        Assert.Equal(1, meta.DiscNumber);
        Assert.Equal(2, meta.DiscCount);
        Assert.Equal(2024, meta.Year);
        Assert.Equal("Composer", meta.Composer);
        Assert.Equal("Comment", meta.Comment);
        Assert.Equal("Copyright", meta.Copyright);
        Assert.Equal("Publisher", meta.Publisher);
        Assert.Equal(120, meta.BeatsPerMinute);
        Assert.NotNull(meta.Duration);
        Assert.True(meta.Duration > TimeSpan.Zero);
        Assert.NotNull(meta.SampleRate);
        Assert.NotNull(meta.ChannelCount);
        Assert.Empty(meta.Artwork);
    }

    [Fact]
    public void Read_NoTags_ReturnsUnavailableFieldsNotFailure()
    {
        var meta = _reader.Read(MetadataFixtureHelper.WavNoTags);

        Assert.Null(meta.Title);
        Assert.Empty(meta.Artists);
        Assert.Null(meta.Album);
        Assert.Null(meta.Genre);
        Assert.Empty(meta.Artwork);
        Assert.NotNull(meta.Duration);
    }

    [Fact]
    public void Read_MissingTitle_DoesNotInventFileName()
    {
        var meta = _reader.Read(MetadataFixtureHelper.WavMissingTitle);
        var stem = Path.GetFileNameWithoutExtension(MetadataFixtureHelper.WavMissingTitle);

        Assert.Null(meta.Title);
        Assert.NotEqual(stem, meta.Title);
        Assert.Equal(["Only Artist"], meta.Artists);
    }

    [Fact]
    public void Read_UnicodeTags_Preserved()
    {
        var meta = _reader.Read(MetadataFixtureHelper.WavUnicode);

        Assert.Equal("タイトル", meta.Title);
        Assert.Equal(["アーティスト"], meta.Artists);
        Assert.Equal("アルバム", meta.Album);
    }

    [Fact]
    public void Read_ArtistWithSemicolon_IsSingleValue()
    {
        var meta = _reader.Read(MetadataFixtureHelper.WavPunctuationArtist);

        Assert.Equal(["AC;DC Tribute"], meta.Artists);
        Assert.Equal(["Rock;Roll Legacy"], meta.Genres);
        Assert.DoesNotContain(meta.Artists, a => a == "AC");
        Assert.DoesNotContain(meta.Genres, g => g == "Rock");
    }

    [Fact]
    public void Read_NativeArtistsAdditionalField_SplitsOnDisplaySeparator()
    {
        var meta = _reader.Read(MetadataFixtureHelper.WavMultiArtistNative);

        Assert.Equal(["First", "Second"], meta.Artists);
    }

    [Fact]
    public void Read_DefaultOptions_DoesNotExposeOrCopyArtwork()
    {
        var meta = _reader.Read(MetadataFixtureHelper.WavWithLargeArt);
        Assert.Empty(meta.Artwork);

        // ATL keeps the EmbeddedPictures backing store unloaded until the property is accessed.
        var track = new ATL.Track(MetadataFixtureHelper.WavWithLargeArt, load: true);
        var field = typeof(ATL.Track).GetField(
            "<currentEmbeddedPictures>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Null(field.GetValue(track));

        var withArt = _reader.Read(
            MetadataFixtureHelper.WavWithLargeArt,
            new AudioMetadataReadOptions { IncludeArtwork = true });
        Assert.NotEmpty(withArt.Artwork);
        Assert.True(withArt.Artwork[0].Data.Length >= 256 * 1024);
    }

    [Fact]
    public void Read_IncludeArtwork_ReturnsOwnedFrontCoverFirst()
    {
        var meta = _reader.Read(
            MetadataFixtureHelper.WavFrontBackArt,
            new AudioMetadataReadOptions { IncludeArtwork = true });

        Assert.Equal(2, meta.Artwork.Count);
        Assert.Equal(AudioArtworkKind.FrontCover, meta.Artwork[0].Kind);
        Assert.Equal(AudioArtworkKind.BackCover, meta.Artwork[1].Kind);
        Assert.True(meta.Artwork[0].Data.Length > 0);

        // Mutating the returned list must be impossible / not affect subsequent reads.
        Assert.ThrowsAny<Exception>(() => ((IList)meta.Artwork).Add(meta.Artwork[0]));
    }

    [Fact]
    public void ReadArtwork_ReturnsOwnedBytes_IndependentOfFile()
    {
        var artwork = _reader.ReadArtwork(MetadataFixtureHelper.WavFrontBackArt);
        Assert.NotEmpty(artwork);

        var first = artwork[0];
        Assert.True(first.Data.Length > 0);

        // Re-open exclusively to prove the reader released the handle.
        using var stream = new FileStream(
            MetadataFixtureHelper.WavFrontBackArt,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _reader.Read(Path.Combine(MetadataFixtureHelper.Root, "missing-file.wav")));
    }

    [Fact]
    public void Read_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _reader.Read(" "));
    }

    [Fact]
    public void Read_DoesNotModifyFile()
    {
        var path = MetadataFixtureHelper.WavBasic;
        var before = MetadataFixtureHelper.Sha256(path);
        _ = _reader.Read(path, new AudioMetadataReadOptions { IncludeArtwork = true });
        var after = MetadataFixtureHelper.Sha256(path);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Read_ConcurrentIndependentReads_Succeed()
    {
        var paths = new[]
        {
            MetadataFixtureHelper.WavBasic,
            MetadataFixtureHelper.WavUnicode,
            MetadataFixtureHelper.WavNoTags,
            MetadataFixtureHelper.WavPunctuationArtist,
        };

        Parallel.For(0, 32, i =>
        {
            var path = paths[i % paths.Length];
            var meta = _reader.Read(path);
            Assert.NotNull(meta);
        });
    }

    [Fact]
    public void Collections_AreImmutableSnapshots()
    {
        var meta = _reader.Read(MetadataFixtureHelper.WavBasic);
        Assert.ThrowsAny<Exception>(() => ((IList)meta.Artists).Add("x"));
        Assert.ThrowsAny<Exception>(() => ((IDictionary)meta.AdditionalTags).Add("k", "v"));
    }
}
