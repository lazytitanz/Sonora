namespace Sonora.TestPlayer.Services;

/// <summary>
/// Folder browser for queue import. Defaults to the user's Music folder and remembers the last choice.
/// </summary>
public sealed class MusicFolderPicker
{
    private readonly Control _owner;
    private string _initialDirectory;

    public MusicFolderPicker(Control owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _initialDirectory = ResolveDefaultMusicFolder();
    }

    /// <summary>Directory shown the next time the dialog opens (Music by default, then last pick).</summary>
    public string InitialDirectory => _initialDirectory;

    /// <summary>
    /// Shows a folder browser starting at <see cref="InitialDirectory"/>.
    /// Returns the selected path, or <see langword="null"/> when cancelled.
    /// </summary>
    public string? PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Add audio folder (includes subfolders)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            InitialDirectory = _initialDirectory,
        };

        // SelectedPath pre-selects the tree node when the path exists.
        if (Directory.Exists(_initialDirectory))
        {
            dialog.SelectedPath = _initialDirectory;
        }

        if (dialog.ShowDialog(_owner) != DialogResult.OK)
        {
            return null;
        }

        var selected = dialog.SelectedPath;
        if (!string.IsNullOrWhiteSpace(selected) && Directory.Exists(selected))
        {
            _initialDirectory = selected;
        }

        return selected;
    }

    public static string ResolveDefaultMusicFolder()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(music) && Directory.Exists(music))
        {
            return music;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var fallback = Path.Combine(profile, "Music");
            if (Directory.Exists(fallback))
            {
                return fallback;
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
