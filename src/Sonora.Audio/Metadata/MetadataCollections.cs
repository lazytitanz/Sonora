namespace Sonora.Audio.Metadata;

/// <summary>
/// Helpers that produce truly immutable collections for public metadata surfaces.
/// </summary>
internal static class MetadataCollections
{
    private const int MaxStringLength = 16 * 1024;
    private const int MaxAdditionalTagCount = 256;
    private const int MaxAdditionalTagKeyLength = 256;

    public static IReadOnlyList<string> FreezeStrings(IEnumerable<string> values)
    {
        List<string>? list = null;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            list ??= [];
            list.Add(Truncate(value.Trim(), MaxStringLength));
        }

        if (list is null || list.Count == 0)
        {
            return Array.Empty<string>();
        }

        return list.Count == 1
            ? new[] { list[0] }
            : Array.AsReadOnly(list.ToArray());
    }

    public static IReadOnlyList<string> SingleOrEmpty(string? value)
    {
        var normalized = NormalizeString(value);
        return normalized is null ? Array.Empty<string>() : new[] { normalized };
    }

    public static IReadOnlyList<AudioArtwork> FreezeArtwork(IEnumerable<AudioArtwork> artwork)
    {
        var array = artwork.ToArray();
        return array.Length == 0 ? Array.Empty<AudioArtwork>() : Array.AsReadOnly(array);
    }

    public static IReadOnlyDictionary<string, string> FreezeTags(
        IEnumerable<KeyValuePair<string, string>> tags)
    {
        Dictionary<string, string>? map = null;
        foreach (var pair in tags)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                continue;
            }

            var key = Truncate(pair.Key.Trim(), MaxAdditionalTagKeyLength);
            var value = Truncate(pair.Value, MaxStringLength);
            map ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (map.Count >= MaxAdditionalTagCount && !map.ContainsKey(key))
            {
                continue;
            }

            map[key] = value;
        }

        if (map is null || map.Count == 0)
        {
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(0));
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(map, StringComparer.Ordinal));
    }

    public static string? NormalizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Truncate(value.Trim(), MaxStringLength);
    }

    public static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}
