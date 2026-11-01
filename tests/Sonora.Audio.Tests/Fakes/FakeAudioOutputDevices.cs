using Sonora.Audio.Devices;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioOutputDevices : IAudioOutputDevices
{
    private readonly object _sync = new();
    private List<AudioOutputDevice> _devices;
    private string? _defaultId;
    private bool _disposed;

    public FakeAudioOutputDevices(IEnumerable<AudioOutputDevice>? devices = null, string? defaultId = null)
    {
        _devices = devices?.ToList() ?? new List<AudioOutputDevice>();
        _defaultId = defaultId ?? _devices.FirstOrDefault(d => d.IsDefault)?.Id ?? _devices.FirstOrDefault()?.Id;
        RefreshDefaultFlags();
    }

    public event EventHandler? DevicesChanged;
    public event EventHandler<DefaultOutputDeviceChangedEventArgs>? DefaultDeviceChanged;

    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            return _devices.ToArray();
        }
    }

    public AudioOutputDevice? GetDefaultDevice()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_defaultId is null)
            {
                return null;
            }

            return _devices.FirstOrDefault(d =>
                string.Equals(d.Id, _defaultId, StringComparison.Ordinal)
                && d.State == AudioDeviceState.Active);
        }
    }

    public bool TryGetDevice(string deviceId, out AudioOutputDevice? device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            device = _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal));
            return device is not null;
        }
    }

    public void SetDevices(IEnumerable<AudioOutputDevice> devices, string? defaultId = null)
    {
        lock (_sync)
        {
            _devices = devices.ToList();
            _defaultId = defaultId ?? _devices.FirstOrDefault(d => d.IsDefault)?.Id ?? _devices.FirstOrDefault()?.Id;
            RefreshDefaultFlags();
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDefault(string? deviceId)
    {
        string? previous;
        lock (_sync)
        {
            previous = _defaultId;
            _defaultId = deviceId;
            RefreshDefaultFlags();
        }

        DefaultDeviceChanged?.Invoke(this, new DefaultOutputDeviceChangedEventArgs(previous, deviceId));
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateDevice(AudioOutputDevice device)
    {
        lock (_sync)
        {
            var index = _devices.FindIndex(d => string.Equals(d.Id, device.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _devices[index] = device;
            }
            else
            {
                _devices.Add(device);
            }

            RefreshDefaultFlags();
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _disposed = true;

    private void RefreshDefaultFlags()
    {
        _devices = _devices
            .Select(d => new AudioOutputDevice(
                d.Id,
                d.Name,
                _defaultId is not null && string.Equals(d.Id, _defaultId, StringComparison.Ordinal),
                d.State))
            .ToList();
    }
}
