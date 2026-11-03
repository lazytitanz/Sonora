using System.Diagnostics;
using System.Text;
using Sonora.Audio.Windows;
using Sonora.TestPlayer.Bridge;
using Sonora.TestPlayer.Controllers;
using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;
using Sonora.TestPlayer.Services;
using Vetra;
using Vetra.WinForms;

namespace Sonora.TestPlayer;

/// <summary>
/// Hosts the Vetra web view and owns the lifetime of the controllers. Start-up failures always surface as a message box
/// followed by closing the form, never a blank window.
/// </summary>
public sealed class MainForm : Form
{
    private const int PositionIntervalMs = 200;
    private const int ResourceIntervalMs = 1500;

    private readonly VetraHost _host;
    private readonly DiagnosticEventStore _store = new();
    private readonly ProcessResourceSampler _sampler = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = PositionIntervalMs };
    private readonly System.Windows.Forms.Timer _resourceTimer = new() { Interval = ResourceIntervalMs };

    private readonly string[] _startupPaths;

    private PlaybackController? _playback;
    private DeviceController? _devices;
    private StreamingController? _streaming;
    private AppBridge? _appBridge;
    private PlaybackStallWatchdog? _stallWatchdog;
    private bool _shuttingDown;
    private bool _shutdownComplete;

    /// <summary>Creates the main window.</summary>
    /// <param name="startupPaths">
    /// Optional local audio paths to load after the engine starts (for example from the command line).
    /// The first existing file is loaded into the player; additional existing files are queued.
    /// </param>
    public MainForm(string[]? startupPaths = null)
    {
        _startupPaths = startupPaths ?? [];
        Text = "Sonora Test Player";
        Width = 1280;
        Height = 840;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        _host = new VetraHost { Dock = DockStyle.Fill };
        Controls.Add(_host);

        _positionTimer.Tick += (_, _) => SafeTick(() =>
        {
            _appBridge?.EmitPositionTick();
            _stallWatchdog?.Tick();
        }, "position");
        _resourceTimer.Tick += (_, _) => SafeTick(() => _appBridge?.EmitResourceTick(), "resources");

        Shown += async (_, _) => await InitializeAsync();
    }

    /// <summary>Formats an exception and its inner exceptions (type and message each) for display.</summary>
    public static string DescribeException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var sb = new StringBuilder();
        sb.AppendLine($"{exception.GetType().FullName}: {exception.Message}");

        var depth = 0;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            depth++;
            sb.AppendLine($"{new string(' ', depth * 2)}Inner exception: {inner.GetType().FullName}: {inner.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_shutdownComplete || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            base.OnFormClosing(e);
            return;
        }

        // Asynchronous disposal cannot be awaited inside FormClosing: defer the close until it completes.
        e.Cancel = true;
        if (!_shuttingDown)
        {
            _shuttingDown = true;
            _ = ShutdownThenCloseAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _positionTimer.Dispose();
            _resourceTimer.Dispose();
            _shutdownCts.Dispose();
        }

        base.Dispose(disposing);
    }

    // The Shown event handler is the only async-void-like entry point; all failures are handled inside.
    private async Task InitializeAsync()
    {
        try
        {
            await _host.InitializeAsync(CreateVetraOptions(), _shutdownCts.Token);
            if (_shuttingDown)
            {
                return;
            }

            CreateServices();
            _positionTimer.Start();
            _resourceTimer.Start();
            _store.Add("app", DiagnosticLevel.Info, "app.started", "Sonora Test Player started.");
            await LoadStartupPathsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (_shuttingDown)
            {
                return;
            }

            ShowFatalAndClose(ex);
        }
    }

    private VetraOptions CreateVetraOptions()
    {
        // Always load www from the output directory next to the executable so Debug/Release
        // runs cannot silently pick up a stale or incomplete working-directory copy.
        var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "www"));
        if (!Directory.Exists(webRoot) || !File.Exists(Path.Combine(webRoot, "index.html")))
        {
            throw new DirectoryNotFoundException(
                $"Test Player web assets were not found at '{webRoot}'. Rebuild the project so www/** is copied to the output directory.");
        }

        // Create before Vetra maps the web root so the virtual host can serve cached covers.
        Directory.CreateDirectory(Path.Combine(webRoot, "_artwork"));

        _store.Add(
            "app",
            DiagnosticLevel.Info,
            "app.webRoot",
            $"WebRoot={webRoot}");

        return new VetraOptions
        {
            WebRoot = webRoot,
            StartPage = "index.html",
            EnableDialogs = true,
            EnableWindowControls = true,
            // Storage is fine for user prefs; static JS is cache-busted via ?v= query on script tags.
            EnableStorage = true,
            EnableDevTools = true,
            EnableNotifications = false,
            EnableSystemTray = false,
            CompanyName = "Sonora",
            ApplicationName = "SonoraTestPlayer",
            Logger = new CallbackVetraLogger((level, message) =>
                _store.Add(
                    "vetra",
                    level switch
                    {
                        VetraLogLevel.Debug => DiagnosticLevel.Debug,
                        VetraLogLevel.Warning => DiagnosticLevel.Warning,
                        VetraLogLevel.Error => DiagnosticLevel.Error,
                        _ => DiagnosticLevel.Info,
                    },
                    "vetra.log",
                    message)),
        };
    }

    private async Task LoadStartupPathsAsync()
    {
        if (_playback is null || _startupPaths.Length == 0)
        {
            return;
        }

        var existing = _startupPaths
            .Select(static p => p.Trim().Trim('"'))
            .Where(static p => p.Length > 0 && File.Exists(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (existing.Length == 0)
        {
            return;
        }

        try
        {
            if (existing.Length > 1)
            {
                await _playback.AddFilesAsync(existing).ConfigureAwait(true);
            }

            await _playback.LoadAsync(existing[0]).ConfigureAwait(true);
            _store.Add(
                "app",
                DiagnosticLevel.Info,
                "app.startupLoad",
                $"Loaded startup path '{Path.GetFileName(existing[0])}'" +
                (existing.Length > 1 ? $" (+{existing.Length - 1} queued)." : "."));
        }
        catch (Exception ex)
        {
            _store.Add(
                "app",
                DiagnosticLevel.Warning,
                "app.startupLoadFailed",
                $"Startup load failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CreateServices()
    {
        var context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "www"));
        var artworkCache = new ArtworkFileCache(Path.Combine(webRoot, "_artwork"));
        var metadata = new MetadataFacade(
            SonoraWindows.CreateMetadataReader(),
            (path, ex) => _store.Add(
                "metadata",
                DiagnosticLevel.Warning,
                "metadata.readFailed",
                $"Could not read metadata: {ex.GetType().Name}: {ex.Message}",
                ("path", path)),
            artworkCache);

        var playback = new PlaybackController(_store, context, metadata);
        _playback = playback;
        var engine = playback.CreateEngine();
        var player = playback.CreatePlayer();

        _devices = new DeviceController(engine, player, _store, context);
        var dsp = new DspController(player, _store);
        _streaming = new StreamingController(playback, _store);
        var scenarios = new ScenarioController(_store, _sampler);

        var folderPicker = new MusicFolderPicker(this);
        _appBridge = new AppBridge(
            _host.Bridge,
            context,
            playback,
            _devices,
            dsp,
            _streaming,
            scenarios,
            _store,
            _sampler,
            folderPicker);
        _appBridge.Register();

        _stallWatchdog = new PlaybackStallWatchdog(
            _store,
            () => _playback?.Player,
            () => _playback?.GetSnapshot() ?? new Models.EngineSnapshotDto { CapturedAtUtc = DateTimeOffset.UtcNow },
            (name, payload) =>
            {
                try
                {
                    _appBridge?.EmitUiEvent(name, payload);
                }
                catch
                {
                    // best-effort UI notify
                }
            });
    }

    private void ShowFatalAndClose(Exception exception)
    {
        _store.Add("app", DiagnosticLevel.Error, "app.initFailed", "Initialization failed: " + exception.Message);

        try
        {
            MessageBox.Show(
                this,
                "Sonora Test Player could not start.\n\n" + DescribeException(exception),
                "Sonora Test Player - startup failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception dialogEx)
        {
            Debug.WriteLine($"[MainForm] Could not show startup failure dialog: {dialogEx}");
        }

        Close();
    }

    private async Task ShutdownThenCloseAsync()
    {
        try
        {
            await ShutdownAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainForm] Shutdown failed: {ex}");
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private async Task ShutdownAsync()
    {
        _positionTimer.Stop();
        _resourceTimer.Stop();

        // Cancel in-flight operations first so nothing starts new engine work while we dispose.
        _shutdownCts.Cancel();
        _streaming?.CancelLoad();
        if (_host.IsInitialized)
        {
            _host.Bridge.CancelPending();
        }

        _appBridge?.Dispose();
        _devices?.Dispose();

        if (_playback is { } playback)
        {
            _playback = null;
            await playback.DisposeAsync();
        }
    }

    private void SafeTick(Action tick, string name)
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            tick();
        }
        catch (Exception ex)
        {
            // A failed periodic update must not take down the UI thread.
            Debug.WriteLine($"[MainForm] {name} tick failed: {ex.Message}");
        }
    }
}
