using System.Diagnostics;
using Sonora.TestPlayer.Controllers;
using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;
using Sonora.TestPlayer.Services;
using Vetra.Bridge;

namespace Sonora.TestPlayer.Bridge;

/// <summary>
/// Thin adapter between the Vetra JavaScript bridge and the controllers. Contains no playback logic: every
/// command delegates to a controller, and every pushed event originates from a controller or the diagnostic store.
/// </summary>
/// <remarks>
/// <para>
/// All commands are registered in <see cref="Register"/>. Arguments are positional (a JSON array) with the exact
/// count shown; payloads use camelCase and enums as camelCase strings. Pushed events are named by
/// <see cref="UiEventNames"/>.
/// </para>
/// <para>
/// Outbound events are marshalled with <see cref="SynchronizationContext.Post"/> (never <c>Send</c>) onto the supplied
/// context before calling <c>EmitAsync</c>, so controllers and the engine are never blocked by the web view.
/// </para>
/// </remarks>
public sealed class AppBridge : IDisposable
{
    private readonly IVetraBridge _bridge;
    private readonly SynchronizationContext _context;
    private readonly PlaybackController _playback;
    private readonly DeviceController _devices;
    private readonly DspController _dsp;
    private readonly StreamingController _streaming;
    private readonly ScenarioController _scenarios;
    private readonly DiagnosticEventStore _store;
    private readonly ProcessResourceSampler _sampler;
    private readonly MusicFolderPicker? _folderPicker;
    private int _disposed;

    /// <summary>Creates the bridge adapter. Call <see cref="Register"/> to expose commands and start event forwarding.</summary>
    public AppBridge(
        IVetraBridge bridge,
        SynchronizationContext context,
        PlaybackController playback,
        DeviceController devices,
        DspController dsp,
        StreamingController streaming,
        ScenarioController scenarios,
        DiagnosticEventStore store,
        ProcessResourceSampler sampler,
        MusicFolderPicker? folderPicker = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _scenarios = scenarios ?? throw new ArgumentNullException(nameof(scenarios));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _folderPicker = folderPicker;
    }

    /// <summary>Registers every bridge command and subscribes to controller events.</summary>
    public void Register()
    {
        RegisterApp();
        RegisterPlayback();
        RegisterQueue();
        RegisterDevices();
        RegisterDsp();
        RegisterStreaming();
        RegisterDiagnostics();
        RegisterScenarios();
        WireEvents();
    }

    /// <summary>Pushes a lightweight <see cref="UiEventNames.Position"/> update. Intended for a ~200 ms timer.</summary>
    public void EmitPositionTick()
    {
        var s = _playback.GetSnapshot();
        if (s.Disposed)
        {
            return;
        }

        Emit(UiEventNames.Position, new
        {
            state = s.State,
            positionMs = s.PositionMs,
            bufferedPositionMs = s.BufferedPositionMs,
            durationMs = s.DurationMs,
            hasKnownDuration = s.HasKnownDuration,
            queueIndex = s.QueueIndex,
            nextTrackState = s.NextTrackState,
            networkStatus = s.NetworkStatus,
            networkBytesBuffered = s.NetworkBytesBuffered,
            underrunCount = s.UnderrunCount,
        });
    }

    /// <summary>Takes a process resource sample, feeds soak tracking, and pushes <see cref="UiEventNames.Resources"/>.</summary>
    public void EmitResourceTick()
    {
        var sample = _sampler.Sample();
        _scenarios.ObserveResources(sample);
        Emit(UiEventNames.Resources, new { sample, soak = _scenarios.GetSoakSession() });
    }

    /// <summary>Captures the full diagnostic snapshot (unredacted; redaction happens at export time).</summary>
    public DiagnosticSnapshot CaptureDiagnostics() =>
        DiagnosticSnapshot.Capture(
            _playback.GetSnapshot(),
            _dsp.GetState(),
            _sampler.Latest ?? _sampler.Sample(),
            _store,
            _scenarios.GetReport());

    /// <summary>Stops forwarding events. Commands stay registered until the bridge itself is disposed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _playback.UiEvent -= Emit;
        _devices.UiEvent -= Emit;
        _store.EventAdded -= OnDiagnosticEvent;
    }

    // ---------------------------------------------------------------------------------------------
    // Registration (one place)
    // ---------------------------------------------------------------------------------------------

    private void RegisterApp()
    {
        _bridge.Register("app.getInfo", () => new
        {
            environment = EnvironmentInfoDto.Capture(),
            urlSafetyLimitation = StreamingLimitations.Text,
            musicFolder = _folderPicker?.InitialDirectory ?? MusicFolderPicker.ResolveDefaultMusicFolder(),
        });
    }

    private void RegisterPlayback()
    {
        _bridge.Register("playback.getSnapshot", () => _playback.GetSnapshot());
        _bridge.RegisterAsync<string>("playback.load", (path, ct) => Done(_playback.LoadAsync(path, ct)));
        _bridge.RegisterAsync("playback.play", ct => Done(_playback.PlayAsync(ct)));
        _bridge.Register("playback.pause", () => Run(_playback.Pause));
        _bridge.Register("playback.stop", () => Run(_playback.Stop));
        _bridge.RegisterAsync("playback.next", ct => Box(_playback.NextAsync(ct)));
        _bridge.RegisterAsync("playback.previous", ct => Box(_playback.PreviousAsync(ct)));
        // Off the UI/bridge thread: Media Foundation seeks can block long enough to AppHang the host.
        _bridge.RegisterAsync<double>(
            "playback.seek",
            (positionMs, ct) => Done(Task.Run(() => _playback.Seek(positionMs), ct)));
        _bridge.RegisterAsync<double>(
            "playback.seekWithResult",
            (positionMs, ct) => Box(Task.Run(() => _playback.SeekWithResult(positionMs), ct)));
        _bridge.Register<float>("playback.setVolume", volume => _playback.SetVolume(volume));
        _bridge.Register<bool>("playback.setGapless", enabled => Run(() => _playback.SetGaplessEnabled(enabled)));
        _bridge.RegisterAsync("playback.preloadNext", ct => Done(_playback.PreloadNextAsync(ct)));
        _bridge.Register("playback.clearNext", () => Run(_playback.ClearNext));
        _bridge.RegisterAsync<string>("playback.getArtwork", (id, ct) => Box(_playback.GetArtworkDataUrlAsync(id, ct)));
    }

    private void RegisterQueue()
    {
        _bridge.Register("queue.get", () => _playback.GetQueue());
        _bridge.RegisterAsync<string[]>("queue.addFiles", (paths, ct) => Box(_playback.AddFilesAsync(paths, ct)));
        _bridge.RegisterAsync<string>("queue.addFolder", (path, ct) => Box(_playback.AddFolderAsync(path, ct)));
        _bridge.RegisterAsync("queue.pickAndAddFolder", PickAndAddFolderAsync);
        _bridge.Register<string>("queue.remove", id => _playback.RemoveFromQueue(id));
        _bridge.Register<string, int>("queue.move", (id, newIndex) => _playback.MoveInQueue(id, newIndex));
        _bridge.Register("queue.clear", () => Run(_playback.ClearQueue));
        _bridge.RegisterAsync<string>("queue.play", (id, ct) => Done(_playback.PlayQueueItemAsync(id, ct)));
    }

    private async Task<object?> PickAndAddFolderAsync(CancellationToken cancellationToken)
    {
        if (_folderPicker is null)
        {
            return Array.Empty<object>();
        }

        var folder = await InvokeOnUiAsync(_folderPicker.PickFolder).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null; // cancelled
        }

        var items = await _playback.AddFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        return items;
    }

    private Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(
            _ =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            null);
        return tcs.Task;
    }

    private void RegisterDevices()
    {
        _bridge.Register("devices.list", () => _devices.ListDevices());
        _bridge.RegisterAsync<string?>("devices.set", (deviceId, ct) => Done(_devices.SetDeviceAsync(deviceId, ct)));
        _bridge.Register("output.get", () => _devices.GetOutputState());
        _bridge.RegisterAsync<OutputConfigDto>("output.configure", (request, ct) => Box(_devices.ConfigureOutputAsync(request, ct)));
    }

    private void RegisterDsp()
    {
        _bridge.Register("dsp.getState", () => _dsp.GetState());
        _bridge.Register<bool>("dsp.setRackBypass", bypass => _dsp.SetRackBypass(bypass));
        _bridge.Register<float>("dsp.setPreampGain", gainDb => _dsp.SetPreampGainDb(gainDb));
        _bridge.Register<bool>("dsp.setPreampEnabled", enabled => _dsp.SetPreampEnabled(enabled));
        _bridge.Register<bool>("dsp.setEqEnabled", enabled => _dsp.SetEqualizerEnabled(enabled));
        _bridge.Register<int, float>("dsp.setBandGain", (band, gainDb) => _dsp.SetBandGain(band, gainDb));
        _bridge.Register<int, bool>("dsp.setBandEnabled", (band, enabled) => _dsp.SetBandEnabled(band, enabled));
        _bridge.Register<string>("dsp.applyPreset", name => _dsp.ApplyPreset(name));
        _bridge.Register("dsp.reset", () => _dsp.Reset());
        _bridge.Register<bool>("dsp.setLimiterEnabled", enabled => _dsp.SetLimiterEnabled(enabled));
        _bridge.Register<float>("dsp.setLimiterCeiling", ceilingDb => _dsp.SetLimiterCeilingDb(ceilingDb));
        _bridge.Register<float>("dsp.setLimiterRelease", releaseMs => _dsp.SetLimiterReleaseMs(releaseMs));
    }

    private void RegisterStreaming()
    {
        _bridge.RegisterAsync<string, bool, bool>(
            "stream.load",
            (url, allowLocal, play, ct) => Box(_streaming.LoadUrlAsync(url, allowLocal, play, ct)));
        _bridge.Register("stream.cancel", () => _streaming.CancelLoad());
        _bridge.Register("stream.status", () => _streaming.GetStatus());
    }

    private void RegisterDiagnostics()
    {
        _bridge.Register("diagnostics.snapshot", () => CaptureDiagnostics());
        _bridge.Register<long>("diagnostics.getEvents", afterSeq => _store.Query(afterSeq: afterSeq));
        _bridge.Register("diagnostics.clear", () => Run(_store.Clear));
        _bridge.Register("diagnostics.resources", () => _sampler.Latest ?? _sampler.Sample());
        _bridge.Register<bool, string>("diagnostics.export", (includeSensitive, format) => Export(includeSensitive, format));
        _bridge.RegisterAsync<string, bool, string>(
            "diagnostics.exportToFile",
            async (path, includeSensitive, format, ct) =>
            {
                var content = Export(includeSensitive, format);
                await DiagnosticExporter.WriteToFileAsync(path, content, ct).ConfigureAwait(false);
                return Path.GetFileName(path);
            });
    }

    private void RegisterScenarios()
    {
        _bridge.Register("scenarios.catalog", () => ScenarioController.Catalog);
        _bridge.Register("scenarios.results", () => _scenarios.GetResults());
        _bridge.Register<string, ScenarioStatus, string?>(
            "scenarios.record",
            (id, status, notes) => _scenarios.Record(id, status, notes));
        _bridge.Register("scenarios.reset", () => Run(_scenarios.ResetResults));
        _bridge.Register("soak.get", () => _scenarios.GetSoakSession());
        _bridge.Register("soak.start", () => _scenarios.StartSoak());
        _bridge.Register("soak.stop", () => _scenarios.StopSoak());
        _bridge.Register<string>("soak.note", note => _scenarios.AddSoakNote(note));
    }

    // ---------------------------------------------------------------------------------------------
    // Events
    // ---------------------------------------------------------------------------------------------

    private void WireEvents()
    {
        _playback.UiEvent += Emit;
        _devices.UiEvent += Emit;
        _store.EventAdded += OnDiagnosticEvent;
    }

    private void OnDiagnosticEvent(DiagnosticEventDto evt) => Emit(UiEventNames.DiagnosticEvent, evt);

    private void Emit(string name, object? payload)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Post (never Send): the caller may be an engine/worker thread and must not block on the web view.
        _context.Post(
            state =>
            {
                // EmitCoreAsync contains its own exception handling; the task is intentionally not awaited.
                _ = EmitCoreAsync(name, payload);
            },
            null);
    }

    private async Task EmitCoreAsync(string name, object? payload)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _bridge.IsDisposed)
            {
                return;
            }

            await _bridge.EmitAsync(name, payload).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The web view can disappear during shutdown; a failed push must never fault the sender.
            Debug.WriteLine($"[AppBridge] Emit '{name}' failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private string Export(bool includeSensitive, string format)
    {
        var snapshot = CaptureDiagnostics();
        return string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
            ? DiagnosticExporter.ExportTextSummary(snapshot, includeSensitive)
            : DiagnosticExporter.ExportJson(snapshot, includeSensitive);
    }

    private static object? Run(Action action)
    {
        action();
        return null;
    }

    private static async Task<object?> Done(Task task)
    {
        await task.ConfigureAwait(false);
        return null;
    }

    private static async Task<object?> Box<T>(Task<T> task) => await task.ConfigureAwait(false);
}
