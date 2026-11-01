using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Playback;

namespace Sonora.Audio;

/// <summary>
/// Owns shared engine services and creates <see cref="AudioPlayer"/> instances.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly IAudioDecoderFactory _decoderFactory;
    private readonly IAudioOutputFactory _outputFactory;
    private readonly IAudioOutputDevices _outputDevices;
    private readonly bool _ownsOutputDevices;
    private readonly AudioEngineOptions _options;
    private readonly List<AudioPlayer> _players = new();
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>
    /// Creates an engine with the provided factories.
    /// Prefer platform helpers such as <c>SonoraWindows.CreateEngine()</c> for normal application use.
    /// </summary>
    public AudioEngine(
        IAudioDecoderFactory decoderFactory,
        IAudioOutputFactory outputFactory,
        AudioEngineOptions? options = null,
        IAudioOutputDevices? outputDevices = null)
    {
        _decoderFactory = decoderFactory ?? throw new ArgumentNullException(nameof(decoderFactory));
        _outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));
        _options = options ?? new AudioEngineOptions();
        if (outputDevices is null)
        {
            _outputDevices = NullAudioOutputDevices.Instance;
            _ownsOutputDevices = false;
        }
        else
        {
            _outputDevices = outputDevices;
            _ownsOutputDevices = true;
        }
    }

    /// <summary>Engine options applied to newly created players.</summary>
    public AudioEngineOptions Options => _options;

    /// <summary>
    /// Platform output-device enumeration and change notifications.
    /// On Windows this follows the Multimedia default render role.
    /// </summary>
    public IAudioOutputDevices OutputDevices => _outputDevices;

    /// <summary>Creates a new <see cref="AudioPlayer"/> owned by this engine.</summary>
    public AudioPlayer CreatePlayer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var player = new AudioPlayer(_decoderFactory, _outputFactory, _options, _outputDevices);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _players.Add(player);
        }

        return player;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        AudioPlayer[] players;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            players = _players.ToArray();
            _players.Clear();
        }

        foreach (var player in players)
        {
            player.Dispose();
        }

        if (_ownsOutputDevices)
        {
            _outputDevices.Dispose();
        }
    }
}
