using NAudio.CoreAudioApi;
using Sonora.Audio.Devices;

namespace Sonora.Audio.Windows.Devices;

/// <summary>
/// Windows Core Audio render-endpoint enumeration and notifications.
/// Sonora follows the <see cref="Role.Multimedia"/> default render device.
/// Native callbacks are marshaled to the thread pool before raising events.
/// </summary>
public sealed class WindowsAudioOutputDevices : IAudioOutputDevices
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _notifications;
    private readonly object _sync = new();
    private string? _lastDefaultId;
    private int _devicesChangedScheduled;
    private bool _disposed;

    /// <summary>Creates a device manager bound to the system Core Audio enumerator.</summary>
    public WindowsAudioOutputDevices()
    {
        _enumerator = new MMDeviceEnumerator();
        _lastDefaultId = TryGetDefaultId();
        // false => do not marshal onto a captured SynchronizationContext; we post to the thread pool.
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DeviceAdded += OnNativeDevicesChanged;
        _notifications.DeviceRemoved += OnNativeDevicesChanged;
        _notifications.DeviceStateChanged += OnNativeDeviceStateChanged;
        _notifications.DefaultDeviceChanged += OnNativeDefaultDeviceChanged;
    }

    /// <inheritdoc />
    public event EventHandler? DevicesChanged;

    /// <inheritdoc />
    public event EventHandler<DefaultOutputDeviceChangedEventArgs>? DefaultDeviceChanged;

    /// <inheritdoc />
    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? defaultId;
        try
        {
            defaultId = TryGetDefaultId();
        }
        catch
        {
            defaultId = null;
        }

        var list = new List<AudioOutputDevice>();
        MMDeviceCollection collection;
        try
        {
            collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
        }
        catch (Exception ex)
        {
            throw new AudioDeviceException("Failed to enumerate Windows audio output devices.", ex);
        }

        foreach (var device in collection)
        {
            using (device)
            {
                list.Add(ToSnapshot(device, defaultId));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public AudioOutputDevice? GetDefaultDevice()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                return null;
            }

            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return ToSnapshot(device, device.ID);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool TryGetDevice(string deviceId, out AudioOutputDevice? device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            using var mm = _enumerator.GetDevice(deviceId);
            if (mm.DataFlow != DataFlow.Render)
            {
                device = null;
                return false;
            }

            var defaultId = TryGetDefaultId();
            device = ToSnapshot(mm, defaultId);
            return true;
        }
        catch
        {
            device = null;
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifications.DeviceAdded -= OnNativeDevicesChanged;
        _notifications.DeviceRemoved -= OnNativeDevicesChanged;
        _notifications.DeviceStateChanged -= OnNativeDeviceStateChanged;
        _notifications.DefaultDeviceChanged -= OnNativeDefaultDeviceChanged;
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    private void OnNativeDevicesChanged(object? sender, EventArgs e) => ScheduleDevicesChanged();

    private void OnNativeDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e) => ScheduleDevicesChanged();

    private void OnNativeDefaultDeviceChanged(object? sender, NAudio.CoreAudioApi.DefaultDeviceChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Flow != DataFlow.Render || e.Role != Role.Multimedia)
        {
            return;
        }

        // Marshal off the native notification thread immediately.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed)
            {
                return;
            }

            string? previous;
            string? next;
            lock (_sync)
            {
                previous = _lastDefaultId;
                next = TryGetDefaultId();
                if (string.Equals(previous, next, StringComparison.Ordinal))
                {
                    return;
                }

                _lastDefaultId = next;
            }

            try
            {
                DefaultDeviceChanged?.Invoke(this, new DefaultOutputDeviceChangedEventArgs(previous, next));
            }
            catch
            {
                // ignored
            }

            ScheduleDevicesChanged();
        });
    }

    private void ScheduleDevicesChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _devicesChangedScheduled, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // Brief coalesce window for bursty native notifications.
                Thread.Sleep(25);
                if (_disposed)
                {
                    return;
                }

                try
                {
                    DevicesChanged?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // ignored
                }
            }
            finally
            {
                Interlocked.Exchange(ref _devicesChangedScheduled, 0);
            }
        });
    }

    private string? TryGetDefaultId()
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                return null;
            }

            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    internal static AudioDeviceState MapState(DeviceState state) => state switch
    {
        DeviceState.Active => AudioDeviceState.Active,
        DeviceState.Disabled => AudioDeviceState.Disabled,
        DeviceState.Unplugged => AudioDeviceState.Unplugged,
        DeviceState.NotPresent => AudioDeviceState.NotPresent,
        _ => AudioDeviceState.Unknown,
    };

    private static AudioOutputDevice ToSnapshot(MMDevice device, string? defaultId)
    {
        var id = device.ID;
        return new AudioOutputDevice(
            id,
            device.FriendlyName,
            defaultId is not null && string.Equals(id, defaultId, StringComparison.Ordinal),
            MapState(device.State));
    }
}
