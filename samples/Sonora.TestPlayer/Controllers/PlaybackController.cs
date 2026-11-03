using System.Diagnostics;
using Sonora.Audio;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Sources;
using Sonora.Audio.Windows;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.Devices;
using Sonora.Audio.Windows.DSP;
using Sonora.Audio.Windows.Output;
using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;
using Sonora.TestPlayer.Services;

namespace Sonora.TestPlayer.Controllers;

/// <summary>
/// Owns the Sonora <see cref="AudioEngine"/>, its single <see cref="AudioPlayer"/>, and the application-owned queue.
/// </summary>
/// <remarks>
/// <para><b>Ownership.</b> This controller creates and disposes the engine and player (<see cref="DisposeAsync"/>).
/// Other controllers borrow <see cref="Engine"/> / <see cref="Player"/> and must not dispose them.</para>
/// <para><b>Threading.</b> Public methods may be called from any thread. Queue and coordinator state are
/// guarded by a short lock that is never held while calling the engine or raising callbacks. Engine events are
/// never handled on the raising thread: handlers only capture the current queue generation and
/// <see cref="SynchronizationContext.Post"/> to the context supplied at construction (normally the UI context);
/// player APIs are called only from the posted continuation, never inside an engine event handler.
/// <see cref="UiEvent"/> is raised on that context.</para>
/// <para><b>Queue advancement.</b> Gapless: the next queue item is preloaded; <c>TrackTransitioned</c> moves the
/// queue to it. Non-gapless / fallback: <c>PlaybackCompleted</c> loads and plays the next item. Duplicate and stale
/// advancement is prevented by <see cref="QueueAdvanceCoordinator"/>. Stop invalidates the session and clears
/// the preloaded track; it never advances.</para>
/// <para><b>Failure state.</b> If a load fails the player has no source; the queue keeps the requested item as
/// current so Play retries it, and the error is recorded in diagnostics and raised as <see cref="UiEventNames.Error"/>.</para>
/// </remarks>
public sealed class PlaybackController : IAsyncDisposable
{
    private readonly DiagnosticEventStore _store;
    private readonly SynchronizationContext _context;
    private readonly MetadataFacade _metadata;
    private readonly QueueAdvanceCoordinator _coordinator;

    private readonly object _lock = new();
    private readonly List<QueueItem> _queue = [];
    private QueueItem? _current;
    private bool _gaplessEnabled = true;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _preloadCts;
    private int _loadVersion;
    private (string Id, string? Url)? _artworkCache;

    private AudioEngine? _engine;
    private AudioPlayer? _player;
    private TrackingWasapiOutputFactory? _wasapiTracker;
    private int _disposed;

    private int _transitionsObserved;
    private int _completionsObserved;
    private int _completionAdvances;
    private int _errorsObserved;
    private int _underrunEventsObserved;

    /// <summary>
    /// Serializes seek commands and coalesces to the latest target so rapid preset clicks (Duration+10 s)
    /// cannot queue N Media Foundation seeks behind a hung/slow one.
    /// </summary>
    private readonly SemaphoreSlim _seekGate = new(1, 1);
    private int _seekTicket;
    private double _latestSeekMs;

    /// <summary>Creates the controller. Call <see cref="CreateEngine"/> and <see cref="CreatePlayer"/> next.</summary>
    /// <param name="store">Diagnostics sink.</param>
    /// <param name="context">Context to marshal engine events onto (the UI context in the app).</param>
    /// <param name="metadata">Metadata facade (its reader is owned by the caller).</param>
    /// <param name="coordinator">Optional coordinator; a default is created when omitted.</param>
    public PlaybackController(
        DiagnosticEventStore store,
        SynchronizationContext context,
        MetadataFacade metadata,
        QueueAdvanceCoordinator? coordinator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _coordinator = coordinator ?? new QueueAdvanceCoordinator();
    }

    /// <summary>Raised on the marshalling context with an event name from <see cref="UiEventNames"/> and a payload.</summary>
    public event Action<string, object?>? UiEvent;

    /// <summary>The engine, or <see langword="null"/> before <see cref="CreateEngine"/>.</summary>
    public AudioEngine? Engine => _engine;

    /// <summary>The player, or <see langword="null"/> before <see cref="CreatePlayer"/>.</summary>
    public AudioPlayer? Player => _player;

    /// <summary>
    /// Last WASAPI output created by the tracking factory (for managed-boundary PCM diagnostics).
    /// Null before the first output is opened.
    /// </summary>
    internal WasapiAudioOutput? LastWasapiOutput => _wasapiTracker?.LastWasapi;

    /// <summary>Whether the next queue item is preloaded for gapless handoff.</summary>
    public bool GaplessEnabled => Volatile.Read(ref _gaplessEnabled);

    // ---------------------------------------------------------------------------------------------
    // Creation
    // ---------------------------------------------------------------------------------------------

    /// <summary>Creates the Windows engine. May be called once.</summary>
    public AudioEngine CreateEngine(AudioEngineOptions? options = null)
    {
        ThrowIfDisposed();
        if (_engine is not null)
        {
            throw new InvalidOperationException("The engine has already been created.");
        }

        // Same wiring as SonoraWindows.CreateEngine, with a tracking WASAPI factory so
        // developer gapless PCM captures can tap the managed→WASAPI boundary.
        options ??= new AudioEngineOptions();
        if (options.EffectFactory is null || options.FormatConverter is null)
        {
            options = new AudioEngineOptions
            {
                BufferDuration = options.BufferDuration,
                PrimeDuration = options.PrimeDuration,
                Processors = options.Processors,
                EffectFactory = options.EffectFactory ?? new WindowsAudioEffectFactory(),
                FormatConverter = options.FormatConverter ?? new WindowsAudioFormatConverter(),
                HttpStreaming = options.HttpStreaming,
            };
        }

        _wasapiTracker = new TrackingWasapiOutputFactory();
        _engine = new AudioEngine(
            new WindowsAudioDecoderFactory(options.HttpStreaming),
            _wasapiTracker,
            options,
            new WindowsAudioOutputDevices());
        Log(DiagnosticLevel.Info, "engine.created", "Audio engine created (WASAPI tracking enabled for diagnostics).");
        return _engine;
    }

    /// <summary>Creates the single player and subscribes to its playback events. May be called once.</summary>
    public AudioPlayer CreatePlayer()
    {
        ThrowIfDisposed();
        var engine = _engine ?? throw new InvalidOperationException("Call CreateEngine before CreatePlayer.");
        if (_player is not null)
        {
            throw new InvalidOperationException("The player has already been created.");
        }

        var player = engine.CreatePlayer();
        _player = player;
        player.StateChanged += OnStateChanged;
        player.PlaybackCompleted += OnPlaybackCompleted;
        player.TrackTransitioned += OnTrackTransitioned;
        player.ErrorOccurred += OnErrorOccurred;
        player.Underrun += OnUnderrun;
        player.NetworkStatusChanged += OnNetworkStatusChanged;
        Log(DiagnosticLevel.Info, "player.created", "Audio player created.");
        return player;
    }

    // ---------------------------------------------------------------------------------------------
    // Queue
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns a copy of the queue (items carry no artwork).</summary>
    public IReadOnlyList<QueueItem> GetQueue()
    {
        lock (_lock)
        {
            return _queue.ToArray();
        }
    }

    /// <summary>
    /// Reads metadata for the given files and appends them to the queue. Missing files are skipped and reported
    /// in diagnostics. Metadata I/O is blocking, so it runs on a worker thread to keep the calling (UI) thread free.
    /// Directory paths are expanded recursively to supported audio files.
    /// </summary>
    /// <returns>The items that were added.</returns>
    public async Task<IReadOnlyList<QueueItem>> AddFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ThrowIfDisposed();

        // Blocking file I/O per path; offloaded so a large selection cannot freeze the UI thread.
        var items = await Task.Run(
            () =>
            {
                var expanded = ExpandPaths(paths);
                var created = new List<QueueItem>(expanded.Count);
                foreach (var path in expanded)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        Log(DiagnosticLevel.Warning, "queue.skipped", "File not found; not queued.", ("path", path ?? string.Empty));
                        continue;
                    }

                    created.Add(_metadata.CreateQueueItem(path));
                }

                return created;
            },
            cancellationToken).ConfigureAwait(false);

        if (items.Count > 0)
        {
            lock (_lock)
            {
                _queue.AddRange(items);
            }

            OnQueueMutated($"Added {items.Count} item(s).");
        }

        return items;
    }

    /// <summary>
    /// Recursively finds supported audio files under <paramref name="directory"/> and appends them to the queue.
    /// </summary>
    public Task<IReadOnlyList<QueueItem>> AddFolderAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return AddFilesAsync([directory], cancellationToken);
    }

    /// <summary>
    /// Expands a mixed list of files and directories into distinct audio file paths.
    /// Directories are scanned recursively.
    /// </summary>
    private static List<string> ExpandPaths(IReadOnlyList<string> paths)
    {
        var expanded = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var path = raw.Trim().Trim('"');
            if (Directory.Exists(path))
            {
                foreach (var file in AudioFileEnumerator.Enumerate(path, recursive: true))
                {
                    if (seen.Add(file))
                    {
                        expanded.Add(file);
                    }
                }
            }
            else if (seen.Add(path))
            {
                expanded.Add(path);
            }
        }

        return expanded;
    }

    /// <summary>Removes an item from the queue. Removing the playing item does not stop playback.</summary>
    /// <returns><see langword="true"/> when the item existed.</returns>
    public bool RemoveFromQueue(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();

        lock (_lock)
        {
            var index = _queue.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                return false;
            }

            var currentBefore = _coordinator.CurrentIndex;
            _queue.RemoveAt(index);
            if (_current?.Id == id)
            {
                // The playing item left the queue; continue from where it was.
                _coordinator.SetCurrentIndex(index - 1);
            }
            else if (index <= currentBefore)
            {
                _coordinator.SetCurrentIndex(currentBefore - 1);
            }
        }

        OnQueueMutated("Removed item.");
        return true;
    }

    /// <summary>Moves an item to a new index (clamped to the queue bounds).</summary>
    /// <returns><see langword="true"/> when the item existed.</returns>
    public bool MoveInQueue(string id, int newIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();

        lock (_lock)
        {
            var index = _queue.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                return false;
            }

            var item = _queue[index];
            _queue.RemoveAt(index);
            _queue.Insert(Math.Clamp(newIndex, 0, _queue.Count), item);
            RebaseCurrentIndex_NoLock();
        }

        OnQueueMutated("Reordered queue.");
        return true;
    }

    /// <summary>Clears the queue. The loaded track keeps playing but nothing follows it.</summary>
    public void ClearQueue()
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            _queue.Clear();
            _coordinator.SetCurrentIndex(-1);
        }

        OnQueueMutated("Cleared queue.");
    }

    // ---------------------------------------------------------------------------------------------
    // Transport
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads a local file (appending it to the queue when it is not already queued) without starting playback.
    /// </summary>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();

        var id = EnsureQueued(path);
        await LoadQueueItemAsync(id, play: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads and plays a queued item.</summary>
    /// <exception cref="KeyNotFoundException">No queue item has the id.</exception>
    public Task PlayQueueItemAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();
        return LoadQueueItemAsync(id, play: true, cancellationToken);
    }

    /// <summary>
    /// Starts or resumes playback. When nothing is loaded the current (or first) queue item is loaded and played.
    /// </summary>
    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var player = RequirePlayer();

        if (player.HasSource)
        {
            player.Play();
            return;
        }

        string? id;
        lock (_lock)
        {
            id = _current is { } c && _queue.Any(i => i.Id == c.Id)
                ? c.Id
                : _queue.Count > 0 ? _queue[Math.Clamp(_coordinator.CurrentIndex + 1, 0, _queue.Count - 1)].Id : null;
        }

        if (id is null)
        {
            throw new InvalidOperationException("Nothing is loaded and the queue is empty.");
        }

        await LoadQueueItemAsync(id, play: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pauses playback, preserving position.</summary>
    public void Pause()
    {
        ThrowIfDisposed();
        RequirePlayer().Pause();
    }

    /// <summary>
    /// Stops playback and resets position. Invalidates the queue session (so a late completion cannot advance), cancels
    /// any in-flight load or preload, and clears the preloaded next track.
    /// </summary>
    public void Stop()
    {
        ThrowIfDisposed();
        var player = RequirePlayer();

        _coordinator.Invalidate();
        CancelLoadAndPreload();
        player.Stop();
        ClearNextSafe(player);
        Log(DiagnosticLevel.Info, "playback.stopped", "Stopped by request.");
    }

    /// <summary>Advances to the next queued item and plays it.</summary>
    /// <returns><see langword="false"/> when the queue has no next item.</returns>
    public async Task<bool> NextAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string? id;
        lock (_lock)
        {
            id = _coordinator.PeekNextIndex(_queue.Count) is { } next ? _queue[next].Id : null;
        }

        if (id is null)
        {
            return false;
        }

        await LoadQueueItemAsync(id, play: true, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Plays the previous queued item.</summary>
    /// <returns><see langword="false"/> when there is no previous item.</returns>
    public async Task<bool> PreviousAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string? id;
        lock (_lock)
        {
            var previous = _coordinator.CurrentIndex - 1;
            id = previous >= 0 && previous < _queue.Count ? _queue[previous].Id : null;
        }

        if (id is null)
        {
            return false;
        }

        await LoadQueueItemAsync(id, play: true, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Seeks to a position. Invalid positions are rejected by the engine.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="positionMs"/> is negative or not finite.</exception>
    public void Seek(double positionMs, int correlationId = 0)
    {
        ThrowIfDisposed();
        _ = ToPosition(positionMs); // validate before publishing
        Volatile.Write(ref _latestSeekMs, positionMs);
        var ticket = Interlocked.Increment(ref _seekTicket);
        Log(
            DiagnosticLevel.Debug,
            "playback.seek.enter",
            "Seek enter.",
            ("correlationId", correlationId.ToString()),
            ("ticket", ticket.ToString()),
            ("positionMs", positionMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
        _seekGate.Wait();
        try
        {
            ThrowIfDisposed();
            if (ticket != Volatile.Read(ref _seekTicket))
            {
                Log(DiagnosticLevel.Debug, "playback.seek.coalesced", "Seek coalesced.", ("correlationId", correlationId.ToString()));
                return;
            }

            var player = RequirePlayer();
            if (correlationId != 0)
            {
                player.SetNextSeekCorrelationId(correlationId);
            }

            player.Seek(ToPosition(Volatile.Read(ref _latestSeekMs)));
        }
        finally
        {
            _seekGate.Release();
        }
    }

    /// <summary>Seeks and reports requested vs actual landing position.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="positionMs"/> is negative or not finite.</exception>
    public SeekResultDto SeekWithResult(double positionMs, int correlationId = 0)
    {
        ThrowIfDisposed();
        _ = ToPosition(positionMs); // validate before publishing
        Volatile.Write(ref _latestSeekMs, positionMs);
        var ticket = Interlocked.Increment(ref _seekTicket);
        Log(
            DiagnosticLevel.Debug,
            "playback.seek.enter",
            "SeekWithResult enter.",
            ("correlationId", correlationId.ToString()),
            ("ticket", ticket.ToString()),
            ("positionMs", positionMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
        _seekGate.Wait();
        try
        {
            ThrowIfDisposed();
            if (ticket != Volatile.Read(ref _seekTicket))
            {
                // Newer seek owns the gate next — do not invent a landing position.
                // Coalesced must not be confused with a real decoder Unknown accuracy (UI was
                // swallowing every post-gapless Unknown seek as "skipped").
                var skipped = ToPosition(Volatile.Read(ref _latestSeekMs));
                Log(DiagnosticLevel.Debug, "playback.seek.coalesced", "SeekWithResult coalesced.", ("correlationId", correlationId.ToString()));
                return SeekResultDto.CoalescedSkip(skipped.TotalMilliseconds);
            }

            var player = RequirePlayer();
            if (correlationId != 0)
            {
                player.SetNextSeekCorrelationId(correlationId);
            }

            var result = player.SeekWithResult(ToPosition(Volatile.Read(ref _latestSeekMs)));
            var dto = SeekResultDto.From(result);
            Log(
                DiagnosticLevel.Info,
                "playback.seek",
                $"Seek {dto.Accuracy}.",
                ("correlationId", correlationId.ToString()),
                ("requestedMs", dto.RequestedMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)),
                ("actualMs", dto.ActualMs?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"));
            return dto;
        }
        finally
        {
            _seekGate.Release();
        }
    }

    /// <summary>Copies recent seek lifecycle events from the engine (best-effort, non-blocking).</summary>
    public object CaptureSeekTrace(int maxEvents = 256)
    {
        ThrowIfDisposed();
        var player = _player;
        if (player is null)
        {
            return new { events = Array.Empty<object>(), dropped = 0L, overwritten = 0L };
        }

        maxEvents = Math.Clamp(maxEvents, 16, 2048);
        var buffer = new SeekLifecycleEvent[maxEvents];
        var copied = player.CopySeekLifecycleTrace(buffer, out var dropped, out var overwritten);
        var events = new object[copied];
        for (var i = 0; i < copied; i++)
        {
            var e = buffer[i];
            events[i] = new
            {
                sequence = e.Sequence,
                timestamp = e.Timestamp,
                seekId = e.SeekId,
                sessionGeneration = e.SessionGeneration,
                trackGeneration = e.TrackGeneration,
                pipelineId = e.PipelineId,
                stage = e.Stage.ToString(),
                silenceReason = e.SilenceReason.ToString(),
                requestedTicks = e.RequestedTicks,
                actualTicks = e.ActualTicks,
                accuracy = e.Accuracy,
                outcome = e.Outcome,
                ringCount = e.RingCount,
                samplesDecoded = e.SamplesDecoded,
                samplesConsumed = e.SamplesConsumed,
                flags = e.Flags,
            };
        }

        return new { events, dropped, overwritten, frequency = System.Diagnostics.Stopwatch.Frequency };
    }

    /// <summary>Sets linear volume and returns the authoritative engine value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Volume is outside 0–1 or not finite.</exception>
    public float SetVolume(float volume)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(volume) || volume is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "Volume must be between 0 and 1.");
        }

        var player = RequirePlayer();
        player.Volume = volume;
        return player.Volume;
    }

    // ---------------------------------------------------------------------------------------------
    // Gapless / preload
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Enables or disables gapless preloading. When disabled the preloaded track is cleared and
    /// advancement relies on <c>PlaybackCompleted</c> (a gap between tracks is expected).
    /// </summary>
    public void SetGaplessEnabled(bool enabled)
    {
        ThrowIfDisposed();
        Volatile.Write(ref _gaplessEnabled, enabled);
        if (enabled)
        {
            _ = PreloadNextAsync(CancellationToken.None);
        }
        else
        {
            ClearNext();
        }

        Log(DiagnosticLevel.Info, "gapless.mode", enabled ? "Gapless preload enabled." : "Gapless preload disabled.");
        RaiseSnapshot();
    }

    /// <summary>
    /// Preloads the queue item after the current one into the engine's next-track slot. No-op when gapless is disabled,
    /// nothing is loaded, or there is no next item. Failures are recorded in diagnostics and do not affect playback.
    /// </summary>
    public async Task PreloadNextAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || _player is not { } player)
        {
            return;
        }

        if (!GaplessEnabled || !player.HasSource)
        {
            return;
        }

        QueueItem? next;
        int generation;
        lock (_lock)
        {
            var nextIndex = _coordinator.PeekNextIndex(_queue.Count);
            next = nextIndex is { } i ? _queue[i] : null;
            generation = _coordinator.Generation;
        }

        if (next is null)
        {
            return;
        }

        // Already preloaded (or loading) for the right track: leave the in-flight preload alone.
        if (string.Equals(player.NextTrackPath, next.Path, StringComparison.OrdinalIgnoreCase)
            && player.NextTrackState is NextTrackState.Ready or NextTrackState.Loading)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_lock)
        {
            if (!_coordinator.IsCurrent(generation))
            {
                return;
            }

            _preloadCts?.Cancel();
            _preloadCts = cts = new CancellationTokenSource();
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            await player.PreloadNextAsync(next.Path, linked.Token).ConfigureAwait(false);
            if (!_coordinator.IsCurrent(generation))
            {
                // A load/stop happened while preloading; the engine drops stale preloads itself, but clear to be safe.
                ClearNextSafe(player);
            }
            else
            {
                Log(DiagnosticLevel.Debug, "gapless.preloaded", "Next track preload finished.", ("next", next.Title));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer preload, queue edit, load, or stop.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race.
        }
        catch (Exception ex)
        {
            Log(DiagnosticLevel.Warning, "gapless.preloadFailed", "Next track preload failed: " + Describe(ex), ("next", next.Title));
        }
    }

    /// <summary>Cancels any preload and clears the engine's next-track slot.</summary>
    public void ClearNext()
    {
        if (Volatile.Read(ref _disposed) != 0 || _player is not { } player)
        {
            return;
        }

        lock (_lock)
        {
            _preloadCts?.Cancel();
            _preloadCts = null;
        }

        ClearNextSafe(player);
    }

    // ---------------------------------------------------------------------------------------------
    // Streaming support (used by StreamingController)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads an arbitrary source that is not part of the queue (for example an HTTP stream). The queue position is
    /// detached, so completion does not advance the queue.
    /// </summary>
    /// <param name="source">Source to load.</param>
    /// <param name="item">Display item describing the source.</param>
    /// <param name="play">Whether to start playback after loading.</param>
    public async Task LoadDetachedSourceAsync(
        AudioSource source,
        QueueItem item,
        bool play,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();
        await LoadCoreAsync(source, item, queueIndex: -1, expectedGeneration: null, play, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // Snapshot / artwork
    // ---------------------------------------------------------------------------------------------

    /// <summary>Captures the current player and queue state.</summary>
    public EngineSnapshotDto GetSnapshot()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new EngineSnapshotDto { CapturedAtUtc = DateTimeOffset.UtcNow, Disposed = true };
        }

        int queueLength;
        int queueIndex;
        string? currentId;
        lock (_lock)
        {
            queueLength = _queue.Count;
            currentId = _current?.Id;
            queueIndex = currentId is null ? -1 : _queue.FindIndex(i => i.Id == currentId);
        }

        var snapshot = new EngineSnapshotDto
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            EngineCreated = _engine is not null,
            CurrentItemId = currentId,
            QueueIndex = queueIndex,
            QueueLength = queueLength,
            GaplessEnabled = GaplessEnabled,
            Generation = _coordinator.Generation,
            TransitionsObserved = Volatile.Read(ref _transitionsObserved),
            CompletionsObserved = Volatile.Read(ref _completionsObserved),
            CompletionAdvances = Volatile.Read(ref _completionAdvances),
            ErrorsObserved = Volatile.Read(ref _errorsObserved),
            UnderrunEventsObserved = Volatile.Read(ref _underrunEventsObserved),
        };

        if (_player is not { } p)
        {
            return snapshot;
        }

        try
        {
            var diagnostics = p.GetDiagnostics();
            return snapshot with
            {
                State = p.State,
                HasSource = p.HasSource,
                SourcePath = p.SourcePath,
                PositionMs = p.Position.TotalMilliseconds,
                BufferedPositionMs = p.BufferedPosition.TotalMilliseconds,
                DurationMs = p.Duration.TotalMilliseconds,
                HasKnownDuration = p.HasKnownDuration,
                Volume = p.Volume,
                NextTrackPath = p.NextTrackPath,
                NextTrackState = p.NextTrackState,
                NetworkStatus = p.NetworkTransportStatus,
                NetworkBytesBuffered = p.NetworkBytesBuffered,
                UnderrunCount = diagnostics.UnderrunCount,
                BufferedSamples = diagnostics.BufferedSamples,
                BufferCapacitySamples = diagnostics.BufferCapacitySamples,
                SamplesDecoded = diagnostics.SamplesDecoded,
                SamplesConsumed = diagnostics.SamplesConsumed,
                SeekInProgress = diagnostics.SeekInProgress,
                EffectsBarrier = diagnostics.EffectsBarrier,
                PumpRunning = diagnostics.PumpRunning,
                RingCompleted = diagnostics.RingCompleted,
                LastSilenceReason = diagnostics.LastSilenceReason,
                LastCallbackTimestamp = diagnostics.LastCallbackTimestamp,
                CallbackCount = diagnostics.CallbackCount,
                LastSeekId = diagnostics.LastSeekId,
                LastSeekTerminalId = diagnostics.LastSeekTerminalId,
                LastPromoteTransitionId = diagnostics.LastPromoteTransitionId,
                StopPumpTimeoutCount = diagnostics.StopPumpTimeoutCount,
                OrphanDecodeWorker = diagnostics.OrphanDecodeWorker,
                TraceDroppedCount = diagnostics.TraceDroppedCount,
                TraceOverwrittenCount = diagnostics.TraceOverwrittenCount,
                OutputDeviceStatus = diagnostics.OutputDeviceStatus,
                ActivePipelineId = diagnostics.ActivePipelineId,
                Output = OutputConfigDto.FromPlayer(p),
            };
        }
        catch (ObjectDisposedException)
        {
            return snapshot with { Disposed = true };
        }
    }

    /// <summary>The item currently loaded (without artwork), or <see langword="null"/>.</summary>
    public QueueItem? GetCurrentItem()
    {
        lock (_lock)
        {
            return _current;
        }
    }

    /// <summary>
    /// Returns the artwork <c>data:</c> URL for a queued or current item, or <see langword="null"/>. Reads the file on a
    /// worker thread (blocking I/O) and caches the most recent result.
    /// </summary>
    public async Task<string?> GetArtworkDataUrlAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();

        QueueItem? item;
        lock (_lock)
        {
            // Only reuse a successful cache entry. A prior null must not permanently hide artwork
            // after a transient read failure (for example a short-lived file share conflict).
            if (_artworkCache is { } cache && cache.Id == id && cache.Url is not null)
            {
                return cache.Url;
            }

            item = _queue.Find(i => i.Id == id) ?? (_current?.Id == id ? _current : null);
        }

        if (item is null || item.IsStream)
        {
            return null;
        }

        // Blocking tag read; offloaded to keep the calling (UI) thread responsive.
        string? url;
        try
        {
            url = await Task.Run(() => _metadata.ReadArtworkDataUrl(item.Path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(DiagnosticLevel.Warning, "artwork.readFailed", $"Artwork read failed for '{item.Title}': {Describe(ex)}", ("path", item.Path));
            return null;
        }

        if (url is null)
        {
            Log(DiagnosticLevel.Debug, "artwork.missing", $"No usable embedded artwork for '{item.Title}'.", ("path", item.Path));
            return null;
        }

        lock (_lock)
        {
            _artworkCache = (id, url);
        }

        Log(DiagnosticLevel.Debug, "artwork.served", $"Artwork ready for '{item.Title}'.", ("url", url));
        return url;
    }

    // ---------------------------------------------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Cancels in-flight operations, unsubscribes from the player, then disposes the player and engine (in that
    /// order). Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _coordinator.Invalidate();
        CancelLoadAndPreload();

        var player = _player;
        var engine = _engine;
        _player = null;
        _engine = null;

        if (player is not null)
        {
            player.StateChanged -= OnStateChanged;
            player.PlaybackCompleted -= OnPlaybackCompleted;
            player.TrackTransitioned -= OnTrackTransitioned;
            player.ErrorOccurred -= OnErrorOccurred;
            player.Underrun -= OnUnderrun;
            player.NetworkStatusChanged -= OnNetworkStatusChanged;
            await DisposeQuietlyAsync(player.DisposeAsync(), "player").ConfigureAwait(false);
        }

        if (engine is not null)
        {
            await DisposeQuietlyAsync(engine.DisposeAsync(), "engine").ConfigureAwait(false);
        }

        _seekGate.Dispose();
    }

    // ---------------------------------------------------------------------------------------------
    // Loading core
    // ---------------------------------------------------------------------------------------------

    private Task LoadQueueItemAsync(string id, bool play, CancellationToken cancellationToken)
    {
        QueueItem item;
        int index;
        lock (_lock)
        {
            index = _queue.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"No queue item with id '{id}'.");
            }

            item = _queue[index];
        }

        return LoadCoreAsync(FileAudioSource.Create(item.Path), item, index, expectedGeneration: null, play, cancellationToken);
    }

    /// <summary>
    /// Shared load path. Begins a new session (invalidating stale events), cancels any earlier load, then loads and
    /// optionally plays. <paramref name="expectedGeneration"/> lets an automatic advance abort if the user acted first.
    /// </summary>
    private async Task LoadCoreAsync(
        AudioSource source,
        QueueItem item,
        int queueIndex,
        int? expectedGeneration,
        bool play,
        CancellationToken cancellationToken)
    {
        var player = RequirePlayer();

        CancellationTokenSource loadCts;
        int version;
        int generation;
        lock (_lock)
        {
            if (expectedGeneration is { } expected && !_coordinator.IsCurrent(expected))
            {
                Log(DiagnosticLevel.Debug, "queue.advanceAborted", "Automatic advance superseded before load.", ("item", item.Title));
                return;
            }

            _loadCts?.Cancel();
            _preloadCts?.Cancel();
            _preloadCts = null;
            _loadCts = loadCts = new CancellationTokenSource();
            version = ++_loadVersion;
            generation = _coordinator.BeginSession(queueIndex);
            _current = item with { ArtworkDataUrl = null };
            _artworkCache = null;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(loadCts.Token, cancellationToken);
            await player.LoadAsync(source, linked.Token).ConfigureAwait(false);

            if (!IsLatestLoad(version))
            {
                return;
            }

            if (play)
            {
                player.Play();
            }

            // Thumbnail for the UI (compact data: URL). Done after Load so a failed decode still leaves tags available.
            var artworkUrl = item.IsStream
                ? null
                : await Task.Run(() => _metadata.ReadArtworkDataUrl(item.Path), linked.Token).ConfigureAwait(false);
            var published = item with { ArtworkDataUrl = artworkUrl };
            lock (_lock)
            {
                if (!IsLatestLoad(version))
                {
                    return;
                }

                _current = published;
                if (artworkUrl is not null)
                {
                    _artworkCache = (published.Id, artworkUrl);
                }
            }

            Log(DiagnosticLevel.Info, "playback.loaded", $"Loaded '{item.Title}'.", ("play", play.ToString()), ("generation", generation.ToString()));
            RaiseUi(UiEventNames.TrackChanged, published);
            RaiseSnapshot();
            _ = PreloadNextAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (!IsLatestLoad(version) || cancellationToken.IsCancellationRequested)
        {
            // Superseded by a newer load/stop, or cancelled by the caller: not an error.
            Log(DiagnosticLevel.Debug, "playback.loadCancelled", $"Load of '{item.Title}' was cancelled.");
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (Exception ex) when (ex is not ObjectDisposedException || Volatile.Read(ref _disposed) == 0)
        {
            Log(DiagnosticLevel.Error, DiagnosticEventNames.PlaybackError, $"Load of '{item.Title}' failed: {Describe(ex)}", ("item", item.Title));
            RaiseUi(UiEventNames.Error, new { message = Describe(ex), source = "load" });
            RaiseSnapshot();
            throw;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_loadCts, loadCts))
                {
                    _loadCts = null;
                }
            }

            loadCts.Dispose();
        }
    }

    private bool IsLatestLoad(int version) => Volatile.Read(ref _loadVersion) == version;

    // ---------------------------------------------------------------------------------------------
    // Engine event handlers (raised on engine threads: capture, then Post — never call the player here)
    // ---------------------------------------------------------------------------------------------

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var generation = _coordinator.Generation;
        Post(() =>
        {
            Log(DiagnosticLevel.Debug, "playback.state", "State changed.", ("generation", generation.ToString()));
            RaiseSnapshot();
        });
    }

    private void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        var generation = _coordinator.Generation;
        Post(() => HandleCompleted(generation));
    }

    private void OnTrackTransitioned(object? sender, TrackTransitionedEventArgs e)
    {
        var generation = _coordinator.Generation;
        Post(() => HandleTransitioned(generation, e));
    }

    private void OnErrorOccurred(object? sender, Exception e)
    {
        Post(() =>
        {
            Interlocked.Increment(ref _errorsObserved);
            Log(DiagnosticLevel.Error, DiagnosticEventNames.PlaybackError, "Engine error: " + Describe(e));
            RaiseUi(UiEventNames.Error, new { message = Describe(e), source = "engine" });
        });
    }

    private void OnUnderrun(object? sender, EventArgs e)
    {
        Post(() =>
        {
            Interlocked.Increment(ref _underrunEventsObserved);
            Log(DiagnosticLevel.Warning, DiagnosticEventNames.Underrun, "Underrun.");
        });
    }

    private void OnNetworkStatusChanged(object? sender, EventArgs e)
    {
        Post(() =>
        {
            if (_player is not { } player)
            {
                return;
            }

            var status = player.NetworkTransportStatus;
            Log(DiagnosticLevel.Info, "network.status", $"Network transport {status}.", ("bytesBuffered", player.NetworkBytesBuffered.ToString()));
            RaiseUi(UiEventNames.NetworkChanged, new { status, bytesBuffered = player.NetworkBytesBuffered });
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Posted handlers (marshalling context)
    // ---------------------------------------------------------------------------------------------

    private void HandleTransitioned(int generation, TrackTransitionedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        QueueAdvanceDecision decision;
        QueueItem? advancedTo = null;
        lock (_lock)
        {
            var paths = new string[_queue.Count];
            for (var i = 0; i < paths.Length; i++)
            {
                paths[i] = _queue[i].Path;
            }

            decision = _coordinator.OnTrackTransitioned(generation, e.TransitionId, e.NextPath, paths);
            if (decision.ShouldAdvance)
            {
                advancedTo = _queue[decision.NextIndex];
                _current = advancedTo;
                _artworkCache = null;
            }
        }

        if (!decision.ShouldAdvance)
        {
            Log(
                DiagnosticLevel.Debug,
                "playback.transitionIgnored",
                $"TrackTransitioned ignored: {decision.Reason}.",
                ("reason", decision.Reason.ToString()),
                ("transitionId", e.TransitionId.ToString()));
            return;
        }

        Interlocked.Increment(ref _transitionsObserved);
        Log(
            DiagnosticLevel.Info,
            DiagnosticEventNames.Transitioned,
            $"Gapless transition to '{advancedTo!.Title}'.",
            ("transitionId", e.TransitionId.ToString()),
            ("previousPath", e.PreviousPath ?? string.Empty),
            ("nextPath", e.NextPath ?? string.Empty));
        // Include cached artwork when available so Duration+10 gapless advances paint immediately;
        // otherwise the UI fetches via getArtwork (payload id is authoritative).
        string? cachedArt = null;
        lock (_lock)
        {
            if (_artworkCache is { } cache && cache.Id == advancedTo!.Id)
            {
                cachedArt = cache.Url;
            }
        }

        RaiseUi(UiEventNames.TrackChanged, advancedTo with { ArtworkDataUrl = cachedArt });
        RaiseSnapshot();
        _ = PreloadNextAsync(CancellationToken.None);
        if (cachedArt is null)
        {
            _ = PrefetchArtworkAsync(advancedTo.Id);
        }
    }

    private async Task PrefetchArtworkAsync(string id)
    {
        try
        {
            await GetArtworkDataUrlAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort warm cache; UI getArtwork remains the source of truth.
        }
    }

    private void HandleCompleted(int generation)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _completionsObserved);

        QueueAdvanceDecision decision;
        QueueItem? next = null;
        lock (_lock)
        {
            decision = _coordinator.OnPlaybackCompleted(generation, _queue.Count);
            if (decision.ShouldAdvance)
            {
                next = _queue[decision.NextIndex];
            }
        }

        Log(
            DiagnosticLevel.Info,
            DiagnosticEventNames.Completed,
            $"Playback completed: {decision.Reason}.",
            ("reason", decision.Reason.ToString()),
            ("advance", decision.ShouldAdvance.ToString()));

        if (decision.ShouldAdvance && next is not null)
        {
            Interlocked.Increment(ref _completionAdvances);
            _ = AdvanceAfterCompletionAsync(next, decision);
        }
        else if (decision.Reason is QueueAdvanceReason.NoMoreTracks)
        {
            RaiseUi(UiEventNames.Completed, new { queueEnded = true, advanced = false });
            RaiseSnapshot();
        }
    }

    private async Task AdvanceAfterCompletionAsync(QueueItem next, QueueAdvanceDecision decision)
    {
        try
        {
            await LoadCoreAsync(
                FileAudioSource.Create(next.Path),
                next,
                decision.NextIndex,
                decision.Generation,
                play: true,
                CancellationToken.None).ConfigureAwait(false);
            RaiseUi(UiEventNames.Completed, new { queueEnded = false, advanced = true });
        }
        catch (Exception ex)
        {
            // LoadCoreAsync already recorded the failure; this fire-and-forget continuation must not surface it as unobserved.
            Debug.WriteLine($"[PlaybackController] Automatic advance failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private string EnsureQueued(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_lock)
        {
            var existing = _queue.Find(i => string.Equals(i.Path, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing.Id;
            }
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Audio file not found.", fullPath);
        }

        var item = _metadata.CreateQueueItem(fullPath);
        lock (_lock)
        {
            _queue.Add(item);
        }

        OnQueueMutated("Added item.");
        return item.Id;
    }

    private void OnQueueMutated(string description)
    {
        // The preloaded "next" may no longer be the queue's next item.
        ClearNext();
        Log(DiagnosticLevel.Info, "queue.changed", description);
        RaiseUi(UiEventNames.QueueChanged, GetQueue());
        RaiseSnapshot();
        _ = PreloadNextAsync(CancellationToken.None);
    }

    private void RebaseCurrentIndex_NoLock()
    {
        if (_current is { } c)
        {
            var index = _queue.FindIndex(i => i.Id == c.Id);
            if (index >= 0)
            {
                _coordinator.SetCurrentIndex(index);
            }
        }
    }

    private void CancelLoadAndPreload()
    {
        lock (_lock)
        {
            _loadCts?.Cancel();
            _preloadCts?.Cancel();
            _preloadCts = null;
            _loadVersion++;
        }
    }

    private void ClearNextSafe(AudioPlayer player)
    {
        try
        {
            player.ClearNext();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race.
        }
    }

    private static TimeSpan ToPosition(double positionMs)
    {
        if (!double.IsFinite(positionMs) || positionMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionMs), positionMs, "Position must be a non-negative number of milliseconds.");
        }

        return TimeSpan.FromMilliseconds(positionMs);
    }

    private AudioPlayer RequirePlayer()
    {
        ThrowIfDisposed();
        return _player ?? throw new InvalidOperationException("The player has not been created.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void Post(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Post (never Send): the engine raises these on its own threads and must not block on the UI thread.
        _context.Post(
            _ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PlaybackController] Posted handler failed: {ex}");
                }
            },
            null);
    }

    private void RaiseUi(string name, object? payload)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            UiEvent?.Invoke(name, payload);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlaybackController] UiEvent handler failed: {ex}");
        }
    }

    private void RaiseSnapshot() => RaiseUi(UiEventNames.Snapshot, GetSnapshot());

    private void Log(DiagnosticLevel level, string name, string message, params (string Key, string Value)[] data) =>
        _store.Add("playback", level, name, message, data);

    private async ValueTask DisposeQuietlyAsync(ValueTask disposal, string what)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(DiagnosticLevel.Error, "dispose.failed", $"Disposing {what} failed: {Describe(ex)}");
        }
    }

    /// <summary>Formats an exception with its type and inner exceptions.</summary>
    internal static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" → ", parts);
    }
}
