using System.Diagnostics;
using Sonora.Audio;
using Sonora.Audio.Devices;
using Sonora.Audio.Output;
using Sonora.Audio.Playback;
using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Controllers;

/// <summary>
/// Output-device and output-mode operations for the test player's single <see cref="AudioPlayer"/>.
/// </summary>
/// <remarks>
/// <para><b>Ownership.</b> The engine and player are owned by <see cref="PlaybackController"/>; this controller only
/// borrows them and unsubscribes in <see cref="Dispose"/>, which must run before the owner is disposed.</para>
/// <para><b>Threading.</b> Methods may be called from any thread. Device and output notifications are never
/// handled on the raising thread: they are <see cref="SynchronizationContext.Post"/>ed to the supplied context and
/// surface through <see cref="UiEvent"/> on that context.</para>
/// </remarks>
public sealed class DeviceController : IDisposable
{
    private readonly AudioEngine _engine;
    private readonly AudioPlayer _player;
    private readonly DiagnosticEventStore _store;
    private readonly SynchronizationContext _context;
    private readonly object _latencyLock = new();
    private readonly SemaphoreSlim _configureGate = new(1, 1);
    private double? _effectiveLatencyMs;
    private int _disposed;

    /// <summary>Creates the controller and subscribes to device/output notifications.</summary>
    public DeviceController(
        AudioEngine engine,
        AudioPlayer player,
        DiagnosticEventStore store,
        SynchronizationContext context)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _context = context ?? throw new ArgumentNullException(nameof(context));

        _engine.OutputDevices.DevicesChanged += OnDevicesChanged;
        _engine.OutputDevices.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _player.OutputDeviceChanged += OnOutputDeviceChanged;
        _player.OutputConfigurationChanged += OnOutputConfigurationChanged;
    }

    /// <summary>Raised on the marshalling context with an event name from <see cref="UiEventNames"/> and a payload.</summary>
    public event Action<string, object?>? UiEvent;

    /// <summary>Lists known render endpoints, flagging the preferred and active ones.</summary>
    public IReadOnlyList<DeviceDto> ListDevices()
    {
        ThrowIfDisposed();
        var preferred = _player.PreferredOutputDeviceId;
        var active = _player.ActiveOutputDeviceId;
        return _engine.OutputDevices.GetDevices()
            .Select(d => DeviceDto.From(d, preferred, active))
            .ToArray();
    }

    /// <summary>
    /// Selects an output endpoint; <see langword="null"/> follows the system default. May briefly interrupt audio.
    /// </summary>
    /// <exception cref="AudioDeviceException">The endpoint cannot be resolved.</exception>
    /// <exception cref="OperationCanceledException">The switch was superseded or cancelled.</exception>
    public async Task SetDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (deviceId is not null && string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id must be null (system default) or a non-empty id.", nameof(deviceId));
        }

        Log(DiagnosticLevel.Info, "device.select", $"Selecting output device {deviceId ?? "(system default)"}.");
        try
        {
            await _player.SetOutputDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log(DiagnosticLevel.Info, "device.selectCancelled", "Device selection was superseded or cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log(DiagnosticLevel.Error, "device.selectFailed", "Device selection failed: " + PlaybackController.Describe(ex));
            throw;
        }
    }

    /// <summary>
    /// Applies shared/exclusive mode, fallback policy, and requested latency. May briefly interrupt audio.
    /// Concurrent configure requests are serialized; overlapping calls wait their turn.
    /// </summary>
    /// <returns>The resulting requested + effective state (also after classified failures that leave the player intact).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Requested latency is outside 5–500 ms.</exception>
    public async Task<OutputConfigDto> ConfigureOutputAsync(OutputConfigDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        await _configureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var configuration = request.ToConfiguration();
            Log(
                DiagnosticLevel.Info,
                "output.configure",
                $"Configuring output: {configuration.Mode}, fallback {configuration.ExclusiveFallback}.",
                ("latencyMs", configuration.RequestedLatency?.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "default"));
            try
            {
                await _player.ConfigureOutputAsync(configuration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log(DiagnosticLevel.Info, "output.configureCancelled", "Output configuration was superseded or cancelled.");
                RaiseUi(UiEventNames.OutputChanged, SafeOutputState());
                throw;
            }
            catch (Exception ex)
            {
                Log(
                    DiagnosticLevel.Error,
                    "output.configureFailed",
                    "Output configuration failed: " + PlaybackController.Describe(ex),
                    ("exceptionType", ex.GetType().FullName ?? ex.GetType().Name),
                    ("failureKind", DescribeFailureKind(ex)));
                var state = SafeOutputState();
                RaiseUi(UiEventNames.OutputChanged, state);
                throw;
            }

            var result = GetOutputState();
            RaiseUi(UiEventNames.OutputChanged, result);
            return result;
        }
        finally
        {
            _configureGate.Release();
        }
    }

    private static string DescribeFailureKind(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is AudioOutputException output)
            {
                return output.FailureKind.ToString();
            }
        }

        return "Unspecified";
    }

    /// <summary>Returns the requested configuration plus the effective mode, fallback reason, and device status.</summary>
    public OutputConfigDto GetOutputState()
    {
        ThrowIfDisposed();
        double? latency;
        lock (_latencyLock)
        {
            latency = _effectiveLatencyMs;
        }

        return OutputConfigDto.FromPlayer(_player, latency);
    }

    /// <summary>Unsubscribes from engine and player notifications. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _engine.OutputDevices.DevicesChanged -= OnDevicesChanged;
        _engine.OutputDevices.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _player.OutputDeviceChanged -= OnOutputDeviceChanged;
        _player.OutputConfigurationChanged -= OnOutputConfigurationChanged;
        _configureGate.Dispose();
    }

    // Handlers: raised on engine/native-notification threads. Capture values, Post, do nothing else.

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        Post(() =>
        {
            Log(DiagnosticLevel.Info, "device.listChanged", "Output device list changed.");
            RaiseUi(UiEventNames.DevicesChanged, new { devices = SafeList() });
        });

    private void OnDefaultDeviceChanged(object? sender, DefaultOutputDeviceChangedEventArgs e)
    {
        var previous = e.PreviousDeviceId;
        var current = e.NewDeviceId;
        Post(() =>
        {
            Log(
                DiagnosticLevel.Info,
                "device.defaultChanged",
                "System default output device changed.",
                ("previous", previous ?? string.Empty),
                ("new", current ?? string.Empty));
            RaiseUi(UiEventNames.DevicesChanged, new { devices = SafeList(), defaultChanged = true });
        });
    }

    private void OnOutputDeviceChanged(object? sender, OutputDeviceChangedEventArgs e)
    {
        var args = e;
        Post(() =>
        {
            Log(
                DiagnosticLevel.Info,
                "device.outputChanged",
                $"Output device event: {args.Reason}, status {args.Status}.",
                ("preferred", args.PreferredDeviceId ?? string.Empty),
                ("active", args.ActiveDeviceId ?? string.Empty),
                ("recoveryIntent", args.RecoveryIntent.ToString()));
            RaiseUi(UiEventNames.OutputChanged, SafeOutputState());
            RaiseUi(UiEventNames.DevicesChanged, new { devices = SafeList(), reason = args.Reason });
        });
    }

    private void OnOutputConfigurationChanged(object? sender, OutputConfigurationChangedEventArgs e)
    {
        var latency = e.EffectiveLatency?.TotalMilliseconds;
        var effectiveMode = e.EffectiveMode;
        var reason = e.FallbackReason;
        Post(() =>
        {
            lock (_latencyLock)
            {
                _effectiveLatencyMs = latency;
            }

            Log(
                reason == ExclusiveFallbackReason.None ? DiagnosticLevel.Info : DiagnosticLevel.Warning,
                "output.configChanged",
                $"Output effective mode {effectiveMode?.ToString() ?? "none"}, fallback reason {reason}.",
                ("effectiveLatencyMs", latency?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"));
            RaiseUi(UiEventNames.OutputChanged, SafeOutputState());
        });
    }

    private IReadOnlyList<DeviceDto> SafeList()
    {
        try
        {
            return ListDevices();
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
    }

    private OutputConfigDto? SafeOutputState()
    {
        try
        {
            return GetOutputState();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private void Post(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Post (never Send) so engine and native notification threads are never blocked on the UI thread.
        _context.Post(
            _ =>
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DeviceController] Posted handler failed: {ex}");
                }
            },
            null);
    }

    private void RaiseUi(string name, object? payload)
    {
        try
        {
            UiEvent?.Invoke(name, payload);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceController] UiEvent handler failed: {ex}");
        }
    }

    private void Log(DiagnosticLevel level, string name, string message, params (string Key, string Value)[] data) =>
        _store.Add("device", level, name, message, data);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
