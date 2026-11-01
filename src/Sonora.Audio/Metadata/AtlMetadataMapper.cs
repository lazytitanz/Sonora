using ATL;
using ATL.AudioData;

namespace Sonora.Audio.Metadata;

/// <summary>
/// Maps ATL track data into Sonora public metadata models. ATL types never escape this type.
/// </summary>
internal static class AtlMetadataMapper
{
    private static readonly HashSet<string> MappedAdditionalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "REPLAYGAIN_TRACK_GAIN",
        "REPLAYGAIN_TRACK_PEAK",
        "REPLAYGAIN_ALBUM_GAIN",
        "REPLAYGAIN_ALBUM_PEAK",
        "MUSICBRAINZ_TRACKID",
        "MUSICBRAINZ TRACK ID",
        "MUSICBRAINZ_ALBUMID",
        "MUSICBRAINZ ALBUM ID",
        "MUSICBRAINZ_ARTISTID",
        "MUSICBRAINZ ARTIST ID",
        "MUSICBRAINZ_RELEASEGROUPID",
        "MUSICBRAINZ RELEASE GROUP ID",
        "MUSICBRAINZ_RELEASETRACKID",
        "ARTISTS",
        "ALBUMARTISTS",
        "COMPILATION",
        "ITUNESCOMPILATION",
        "TCMP",
    };

    public static AudioTrackMetadata MapTrack(Track track, AudioMetadataReadOptions options, bool includeArtwork)
    {
        var artists = ResolvePeople(track.Artist, track.AdditionalFields, "ARTISTS");
        var albumArtists = ResolvePeople(track.AlbumArtist, track.AdditionalFields, "ALBUMARTISTS");
        var genres = ResolveGenres(track.Genre, track.AdditionalFields);

        var title = NormalizeTitle(track);
        var duration = MapDuration(track.DurationMs);
        long? bitrate = null;
        if (track.Bitrate > 0 && !double.IsNaN(track.Bitrate) && !double.IsInfinity(track.Bitrate))
        {
            bitrate = checked((long)Math.Round(track.Bitrate * 1000.0));
        }
        int? sampleRate = track.SampleRate > 0 ? (int)Math.Round(track.SampleRate) : null;
        int? channels = track.ChannelsArrangement is { NbChannels: > 0 } arrangement
            ? arrangement.NbChannels
            : null;

        bool? lossless = track.CodecFamily switch
        {
            AudioDataIOFactory.CF_LOSSLESS => true,
            AudioDataIOFactory.CF_LOSSY => false,
            _ => null,
        };

        var artwork = includeArtwork
            ? MapArtwork(track.EmbeddedPictures, options)
            : Array.Empty<AudioArtwork>();

        return new AudioTrackMetadata
        {
            Title = title,
            Artists = artists,
            Album = MetadataCollections.NormalizeString(track.Album),
            AlbumArtists = albumArtists,
            Genre = genres.Count > 0 ? genres[0] : null,
            Genres = genres,
            TrackNumber = PositiveOrNull(track.TrackNumber),
            TrackCount = PositiveOrNull(track.TrackTotal),
            DiscNumber = PositiveOrNull(track.DiscNumber),
            DiscCount = PositiveOrNull(track.DiscTotal),
            Year = PositiveOrNull(track.Year),
            OriginalReleaseDate = MapOriginalReleaseDate(track),
            Duration = duration,
            Composer = MetadataCollections.NormalizeString(track.Composer),
            Comment = MetadataCollections.NormalizeString(track.Comment),
            Copyright = MetadataCollections.NormalizeString(track.Copyright),
            Publisher = MetadataCollections.NormalizeString(track.Publisher),
            Encoder = MetadataCollections.NormalizeString(
                FirstNonEmpty(track.Encoder, track.EncodedBy)),
            Lyrics = MapLyrics(track.Lyrics),
            BeatsPerMinute = MapBpm(track.BPM),
            IsCompilation = MapCompilation(track),
            Bitrate = bitrate,
            SampleRate = sampleRate,
            ChannelCount = channels,
            IsLossless = lossless,
            ReplayGain = MapReplayGain(track.AdditionalFields),
            MusicBrainz = MapMusicBrainz(track.AdditionalFields),
            Artwork = MetadataCollections.FreezeArtwork(artwork),
            AdditionalTags = MetadataCollections.FreezeTags(MapAdditionalTags(track.AdditionalFields)),
        };
    }

    public static IReadOnlyList<AudioArtwork> MapArtwork(
        IList<PictureInfo>? pictures,
        AudioMetadataReadOptions options)
    {
        if (pictures is null || pictures.Count == 0)
        {
            return Array.Empty<AudioArtwork>();
        }

        var results = new List<AudioArtwork>(Math.Min(pictures.Count, options.MaxArtworkCount));
        foreach (var picture in pictures)
        {
            if (results.Count >= options.MaxArtworkCount)
            {
                break;
            }

            var data = picture.PictureData;
            if (data is null || data.Length == 0 || data.Length > options.MaxArtworkBytes)
            {
                continue;
            }

            // Own the bytes so artwork remains valid after ATL releases the file.
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);

            results.Add(new AudioArtwork(
                MetadataCollections.NormalizeString(picture.MimeType),
                MetadataCollections.NormalizeString(picture.Description),
                MapArtworkKind(picture.PicType),
                copy));
        }

        // Prefer front covers first while preserving relative order within kinds.
        results.Sort(static (a, b) =>
        {
            var ak = a.Kind == AudioArtworkKind.FrontCover ? 0 : 1;
            var bk = b.Kind == AudioArtworkKind.FrontCover ? 0 : 1;
            return ak.CompareTo(bk);
        });

        return MetadataCollections.FreezeArtwork(results);
    }

    /// <summary>
    /// Resolves people lists without blindly splitting on ';'.
    /// Prefers format-native multi-value fields (for example Vorbis ARTISTS) when present.
    /// Otherwise preserves the ATL field as a single value so punctuation remains intact.
    /// </summary>
    private static IReadOnlyList<string> ResolvePeople(
        string? primary,
        IDictionary<string, string>? additional,
        string nativeMultiKey)
    {
        if (additional is not null
            && TryGetAdditional(additional, nativeMultiKey, out var multi)
            && !string.IsNullOrWhiteSpace(multi))
        {
            // Native multi-value fields use ATL's display separator between entries.
            return MetadataCollections.FreezeStrings(
                multi.Split(Settings.DisplayValueSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return MetadataCollections.SingleOrEmpty(primary);
    }

    private static IReadOnlyList<string> ResolveGenres(
        string? primary,
        IDictionary<string, string>? additional)
    {
        // Prefer explicit multi-genre additional fields when present; otherwise keep a single value.
        if (additional is not null
            && TryGetAdditional(additional, "GENRES", out var multi)
            && !string.IsNullOrWhiteSpace(multi))
        {
            return MetadataCollections.FreezeStrings(
                multi.Split(Settings.DisplayValueSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return MetadataCollections.SingleOrEmpty(primary);
    }

    private static string? NormalizeTitle(Track track)
    {
        var title = MetadataCollections.NormalizeString(track.Title);
        if (title is null)
        {
            return null;
        }

        // Defense in depth when ATL.Settings.UseFileNameWhenNoTitle remains true in-process:
        // reject titles that are exactly the file name stem and for which the raw tag was empty
        // cannot be proven — only strip when the path stem equals the title AND AdditionalFields
        // contain no title-like evidence. Prefer Settings disable in AtlSettingsPolicy instead.
        return title;
    }

    private static TimeSpan? MapDuration(double durationMs)
    {
        if (double.IsNaN(durationMs) || double.IsInfinity(durationMs) || durationMs <= 0)
        {
            return null;
        }

        try
        {
            var ms = checked((long)Math.Round(durationMs));
            if (ms <= 0)
            {
                return null;
            }

            return TimeSpan.FromMilliseconds(ms);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static DateOnly? MapOriginalReleaseDate(Track track)
    {
        if (track.OriginalReleaseDate is { } dt && dt > DateTime.MinValue)
        {
            return DateOnly.FromDateTime(dt.Date);
        }

        var year = PositiveOrNull(track.OriginalReleaseYear);
        if (year is null)
        {
            return null;
        }

        try
        {
            return new DateOnly(year.Value, 1, 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? MapLyrics(IList<LyricsInfo>? lyrics)
    {
        if (lyrics is null || lyrics.Count == 0)
        {
            return null;
        }

        foreach (var entry in lyrics)
        {
            var text = MetadataCollections.NormalizeString(entry.UnsynchronizedLyrics);
            if (text is not null)
            {
                return text;
            }
        }

        return null;
    }

    private static double? MapBpm(float? bpm)
    {
        if (bpm is null || bpm <= 0 || float.IsNaN(bpm.Value) || float.IsInfinity(bpm.Value))
        {
            return null;
        }

        return bpm.Value;
    }

    private static bool? MapCompilation(Track track)
    {
        if (track.AdditionalFields is null)
        {
            return null;
        }

        if (TryGetAdditional(track.AdditionalFields, "COMPILATION", out var value)
            || TryGetAdditional(track.AdditionalFields, "ITUNESCOMPILATION", out value)
            || TryGetAdditional(track.AdditionalFields, "TCMP", out value))
        {
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return null;
    }

    private static AudioReplayGain? MapReplayGain(IDictionary<string, string>? fields)
    {
        if (fields is null)
        {
            return null;
        }

        var trackGain = ParseGain(fields, "REPLAYGAIN_TRACK_GAIN");
        var trackPeak = ParsePeak(fields, "REPLAYGAIN_TRACK_PEAK");
        var albumGain = ParseGain(fields, "REPLAYGAIN_ALBUM_GAIN");
        var albumPeak = ParsePeak(fields, "REPLAYGAIN_ALBUM_PEAK");
        if (trackGain is null && trackPeak is null && albumGain is null && albumPeak is null)
        {
            return null;
        }

        return new AudioReplayGain(trackGain, trackPeak, albumGain, albumPeak);
    }

    private static AudioMusicBrainzIds? MapMusicBrainz(IDictionary<string, string>? fields)
    {
        if (fields is null)
        {
            return null;
        }

        var trackId = FirstAdditional(fields, "MUSICBRAINZ_TRACKID", "MUSICBRAINZ TRACK ID", "MUSICBRAINZ_RELEASETRACKID");
        var releaseId = FirstAdditional(fields, "MUSICBRAINZ_ALBUMID", "MUSICBRAINZ ALBUM ID");
        var artistId = FirstAdditional(fields, "MUSICBRAINZ_ARTISTID", "MUSICBRAINZ ARTIST ID");
        var releaseGroupId = FirstAdditional(fields, "MUSICBRAINZ_RELEASEGROUPID", "MUSICBRAINZ RELEASE GROUP ID");
        if (trackId is null && releaseId is null && artistId is null && releaseGroupId is null)
        {
            return null;
        }

        return new AudioMusicBrainzIds(trackId, releaseId, artistId, releaseGroupId);
    }

    private static IEnumerable<KeyValuePair<string, string>> MapAdditionalTags(
        IDictionary<string, string>? fields)
    {
        if (fields is null)
        {
            yield break;
        }

        foreach (var pair in fields)
        {
            if (MappedAdditionalKeys.Contains(pair.Key))
            {
                continue;
            }

            yield return pair;
        }
    }

    private static AudioArtworkKind MapArtworkKind(PictureInfo.PIC_TYPE type) => type switch
    {
        PictureInfo.PIC_TYPE.Front => AudioArtworkKind.FrontCover,
        PictureInfo.PIC_TYPE.Back => AudioArtworkKind.BackCover,
        PictureInfo.PIC_TYPE.Artist or PictureInfo.PIC_TYPE.LeadArtist or PictureInfo.PIC_TYPE.Band
            => AudioArtworkKind.Artist,
        PictureInfo.PIC_TYPE.Leaflet => AudioArtworkKind.Leaflet,
        PictureInfo.PIC_TYPE.CD => AudioArtworkKind.Media,
        _ => AudioArtworkKind.Other,
    };

    private static int? PositiveOrNull(int? value)
        => value is > 0 ? value : null;

    private static int? PositiveOrNull(int value)
        => value > 0 ? value : null;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = MetadataCollections.NormalizeString(value);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    private static bool TryGetAdditional(
        IDictionary<string, string> fields,
        string key,
        out string value)
    {
        foreach (var pair in fields)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = "";
        return false;
    }

    private static string? FirstAdditional(IDictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGetAdditional(fields, key, out var value))
            {
                return MetadataCollections.NormalizeString(value);
            }
        }

        return null;
    }

    private static double? ParseGain(IDictionary<string, string> fields, string key)
    {
        if (!TryGetAdditional(fields, key, out var raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2].Trim();
        }

        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? ParsePeak(IDictionary<string, string> fields, string key)
    {
        if (!TryGetAdditional(fields, key, out var raw))
        {
            return null;
        }

        return double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
