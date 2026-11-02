using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Network;
using Sonora.Audio.Output;
using Sonora.Audio.Playback;
using Sonora.Audio.Seeking;
using Sonora.Audio.Sources;

namespace Sonora.Audio;

/// <summary>
/// Loads audio sources and controls playback. Commands are serialized; decoding runs on a background pump.
/// Events are raised on worker threads and are not marshaled to a UI synchronization context.
/// Re-entering playback commands from event handlers is unsupported and may deadlock.
/// </summary>
/// <remarks>
/// <para>
/// Prefer <see cref="DisposeAsync"/> when disposing from an asynchronous context. Synchronous
/// <see cref="Dispose"/> detaches the session under the command gate and completes teardown
/// (output shutdown, decode-pump stop) only after releasing the gate so completion cannot deadlock
/// against output/network workers that need the gate.
/// </para>
/// <para>
/// <see cref="PlaybackState.Buffering"/> is used only while <see cref="LoadAsync(AudioSource, CancellationToken)"/>
/// prepares a new session. Network stalls use <see cref="NetworkTransportStatus.Buffering"/> and do not
/// change <see cref="State"/> to Buffering.
/// </para>
/// </remarks>
public sealed class AudioPlayer : IDisposable, IAsyncDisposable
{
    private readonly Action? _onDisposed;
    private readonly IAudioDecoderFactory _decoderFactory;
    private readonly IAudioOutputFactory _outputFactory;
    private readonly IAudioOutputDevices _outputDevices;
    private readonly AudioEngineOptions _options;
    private readonly IAudioFormatConverter _formatConverter;
    private readonly AudioEffectRack _effects;
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Serializes decoder rewind/seek so Stop and user Seek cannot re-enter Media Foundation concurrently.</summary>
    private readonly SemaphoreSlim _decoderSeekGate = new(1, 1);
    private readonly object _pipelineSync = new();
    private readonly NextTrackSlot _next = new();

    private PlaybackState _state = PlaybackState.Stopped;
    private float _volume = 1f;
    private int _generation;
    private int _activeTrackGeneration;
    private int _transitionId;
    private int _suppressTransitionIdsAtOrBelow;
    private int _outputSwitchGeneration;
    private bool _disposed;
    private bool _hasSource;
    private bool _completionRaised;
    private long _underrunCount;
    private int _underrunLatched;
    private int _positionFrozen;
    private TimeSpan _frozenAudiblePosition;
    /// <summary>
    /// When 0, end-of-stream notifications from the output device are ignored.
    /// Cleared before explicit <see cref="Stop"/> / teardown so NAudio/WASAPI Stopped
    /// callbacks cannot raise <see cref="PlaybackCompleted"/> after a user stop.
    /// </summary>
    private int _acceptEndOfStreamCompletion;

    /// <summary>
    /// Pause after disposing a live WASAPI output before opening exclusive mode on the
    /// same endpoint. Short values (≤50ms) still allow intermittent native
    /// <c>IAudioRenderClient.GetBuffer</c> AVs (<c>0xC0000005</c>) on some drivers when
    /// Shared→Exclusive follows a device switch while playing.
    /// </summary>
    private static readonly TimeSpan ExclusiveEndpointReleaseGrace = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Extra settle after constructing an exclusive client before <see cref="IAudioOutput.Initialize"/>,
    /// and again before <see cref="IAudioOutput.Play"/> so the first GetBuffer is not immediate.
    /// </summary>
    private static readonly TimeSpan ExclusiveEndpointInitGrace = TimeSpan.FromMilliseconds(100);

    private TrackDecodePipeline? _active;
    private IAudioOutput? _output;
    private TransitionSampleSource? _sampleSource;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _outputSwitchCts;
    private AudioFormat _sessionFormat;
    private string? _sourcePath;
    private AudioSource? _currentSource;
    private bool _hasKnownDuration;

    private INetworkTransportInfo? _networkInfo;
    private volatile NetworkTransportStatus _networkStatus = NetworkTransportStatus.Idle;
    private int _networkBytesBuffered;

    private string? _preferredOutputDeviceId;
    private string? _activeOutputDeviceId;
    private volatile OutputDeviceStatus _outputDeviceStatus = OutputDeviceStatus.Available;
    private OutputRecoveryIntent _outputRecoveryIntent = OutputRecoveryIntent.None;
    private int _recoveryInFlight;

    /// <summary>
    /// Serializes asynchronous output Shutdown/Dispose (e.g. from device-loss handlers) so Load /
    /// Configure / recovery never open a new WASAPI client while a prior exclusive client is still
    /// releasing the endpoint.
    /// </summary>
    private readonly object _outputShutdownSync = new();
    private Task _outputShutdownTask = Task.CompletedTask;

    /// <summary>
    /// Shared Dispose/DisposeAsync completion. Concurrent callers await the same teardown rather
    /// than returning early while the first caller is still shutting down outputs.
    /// </summary>
    private readonly object _disposeSync = new();
    private TaskCompletionSource? _disposeTcs;

    private AudioOutputConfiguration _outputConfiguration = AudioOutputConfiguration.Default;
    private AudioOutputMode? _effectiveOutputMode;
    private ExclusiveFallbackReason _outputFallbackReason = ExclusiveFallbackReason.None;

    private long _timelineOriginSamples;
    private long _samplesDecoded;
    private long _samplesConsumed;
    private TimeSpan _heldPosition;
    private TimeSpan _duration;
    private TimeSpan _deviceClockAnchor;
    private TimeSpan _logicalPositionAtAnchor;
    /// <summary>Bumped on every position rebase (including seek-to-zero) so output rebinds observe seek intent.</summary>
    private int _positionIntentGeneration;

    /// <summary>Creates a player. Prefer <see cref="AudioEngine.CreatePlayer"/>.</summary>
    public AudioPlayer(
        IAudioDecoderFactory decoderFactory,
        IAudioOutputFactory outputFactory,
        AudioEngineOptions? options = null,
        IAudioOutputDevices? outputDevices = null)
        : this(decoderFactory, outputFactory, options, outputDevices, onDisposed: null)
    {
    }

    /// <summary>Creates a player with an optional disposal callback used by <see cref="AudioEngine"/> ownership tracking.</summary>
    internal AudioPlayer(
        IAudioDecoderFactory decoderFactory,
        IAudioOutputFactory outputFactory,
        AudioEngineOptions? options,
        IAudioOutputDevices? outputDevices,
        Action? onDisposed)
    {
        _decoderFactory = decoderFactory ?? throw new ArgumentNullException(nameof(decoderFactory));
        _outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));
        _options = options ?? new AudioEngineOptions();
        _formatConverter = _options.FormatConverter ?? new PassthroughFormatConverter();
        _effects = new AudioEffectRack(_options.EffectFactory);
        _outputDevices = outputDevices ?? NullAudioOutputDevices.Instance;
        _onDisposed = onDisposed;
        _outputDevices.DevicesChanged += OnDevicesChanged;
        _outputDevices.DefaultDeviceChanged += OnDefaultDeviceChanged;
    }

    /// <summary>
    /// Per-player DSP effect rack (equalizer, preamp, limiter). Processing runs after the PCM ring
    /// on the output path so live parameter changes apply without waiting for the buffer to drain.
    /// Effect settings are preserved across gapless transitions and track loads; filter state is reset on seek/stop/load.
    /// </summary>
    public IAudioEffectRack Effects => _effects;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised exactly once when playback reaches the natural end of the session with no ready next track.
    /// Not raised between gaplessly joined tracks.
    /// </summary>
    public event EventHandler? PlaybackCompleted;

    /// <summary>
    /// Raised after a gapless consumption-boundary handoff to the preloaded next track.
    /// Never invoked on the real-time audio callback; delivery may lag after the first samples of the
    /// next track have already entered the output pipeline. Not sample-synchronized with audible output.
    /// </summary>
    public event EventHandler<TrackTransitionedEventArgs>? TrackTransitioned;

    /// <summary>Raised when a recoverable or fatal playback error occurs.</summary>
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>Raised once per underrun episode while playing.</summary>
    public event EventHandler? Underrun;

    /// <summary>
    /// Raised when <see cref="NetworkTransportStatus"/> or <see cref="NetworkBytesBuffered"/> changes
    /// for a network-backed source. Delivered from the HTTP producer thread, never the WASAPI callback.
    /// </summary>
    public event EventHandler? NetworkStatusChanged;

    /// <summary>
    /// Raised when the preferred or active output endpoint, device status, or recovery intent changes.
    /// Never raised on the WASAPI callback or native device-notification thread.
    /// </summary>
    public event EventHandler<OutputDeviceChangedEventArgs>? OutputDeviceChanged;

    /// <summary>
    /// Raised when requested or effective output mode / fallback state changes.
    /// Never raised on the WASAPI callback or while holding the player command gate.
    /// </summary>
    public event EventHandler<OutputConfigurationChangedEventArgs>? OutputConfigurationChanged;

    /// <summary>Current playback state.</summary>
    public PlaybackState State => _state;

    /// <summary>Whether a source is loaded.</summary>
    public bool HasSource => Volatile.Read(ref _hasSource);

    /// <summary>
    /// Identifier of the logically active track (filesystem path or absolute URI).
    /// Updates at the output-consumption handoff.
    /// </summary>
    public string? SourcePath => Volatile.Read(ref _sourcePath);

    /// <summary>Typed source of the logically active track, when loaded.</summary>
    public AudioSource? CurrentSource => _currentSource;

    /// <summary>Path of the pending next track when loading or ready; otherwise <see langword="null"/>.</summary>
    public string? NextTrackPath => _next.Path;

    /// <summary>State of the single pending next-track preload slot.</summary>
    public NextTrackState NextTrackState => _next.State;

    /// <summary>Active track duration when known (updates at the consumption handoff).</summary>
    public TimeSpan Duration => _duration;

    /// <summary>
    /// Whether <see cref="Duration"/> is authoritative for the active track.
    /// Network streams without Content-Length / media headers may report <see langword="false"/>.
    /// </summary>
    public bool HasKnownDuration => _hasKnownDuration;

    /// <summary>Network transport status for the active source, or <see cref="NetworkTransportStatus.Idle"/>.</summary>
    public NetworkTransportStatus NetworkTransportStatus => _networkStatus;

    /// <summary>Bytes currently buffered by the network transport, when applicable.</summary>
    public int NetworkBytesBuffered => Volatile.Read(ref _networkBytesBuffered);

    /// <summary>
    /// Application-preferred output endpoint id. <see langword="null"/> means follow the system default
    /// (Windows: Multimedia render role).
    /// </summary>
    public string? PreferredOutputDeviceId => Volatile.Read(ref _preferredOutputDeviceId);

    /// <summary>Currently bound output endpoint id, when known.</summary>
    public string? ActiveOutputDeviceId => Volatile.Read(ref _activeOutputDeviceId);

    /// <summary>Device-binding health, independent of <see cref="State"/>.</summary>
    public OutputDeviceStatus OutputDeviceStatus => _outputDeviceStatus;

    /// <summary>
    /// Whether playback should resume when output becomes available after device loss.
    /// Cleared by <see cref="Stop"/>, <see cref="LoadAsync(AudioSource, CancellationToken)"/>, and <see cref="Dispose"/>.
    /// </summary>
    public OutputRecoveryIntent OutputRecoveryIntent => _outputRecoveryIntent;

    /// <summary>Application-requested output configuration (shared by default).</summary>
    public AudioOutputConfiguration OutputConfiguration => _outputConfiguration;

    /// <summary>
    /// Mode of the currently attached output, or <see langword="null"/> when no output is attached.
    /// </summary>
    public AudioOutputMode? EffectiveOutputMode => _effectiveOutputMode;

    /// <summary>
    /// Why shared fallback is active, or the classified reason for the last exclusive failure.
    /// </summary>
    public ExclusiveFallbackReason OutputFallbackReason => _outputFallbackReason;

    /// <summary>
    /// Estimated audible playback position within the active track. Accounts for device rendered clock
    /// and output latency when available. This is an estimate of what is being heard — not the last
    /// seek request, not the verified decoded landing, and not sample-accurate under shared-mode
    /// resampling, device buffering, underruns, gapless handoffs, or output device switches.
    /// Use <see cref="SeekWithResult"/> for requested vs actual decoded seek positions.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            if (!_hasSource || _active is null)
            {
                return TimeSpan.Zero;
            }

            var state = _state;
            if (state is PlaybackState.Stopped or PlaybackState.Paused)
            {
                return _heldPosition;
            }

            return EstimateAudiblePosition();
        }
    }

    /// <summary>
    /// Timeline position of the latest sample written into the active track's PCM ring (decode frontier).
    /// </summary>
    public TimeSpan BufferedPosition
    {
        get
        {
            if (!_hasSource || _active is null)
            {
                return TimeSpan.Zero;
            }

            var samples = _timelineOriginSamples + Interlocked.Read(ref _samplesDecoded);
            return ClampToDuration(_active.Format.SamplesToTime(samples));
        }
    }

    /// <summary>
    /// Linear master gain in the range [0, 1]. Applied on the output pull path (after the PCM ring,
    /// before the effect rack) so changes are audible on the next device callback rather than after
    /// the ring drains. Distinct from <see cref="Effects"/> preamp gain (dB).
    /// </summary>
    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set
        {
            if (value is < 0f or > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Volume must be between 0 and 1.");
            }

            Volatile.Write(ref _volume, value);
        }
    }

    /// <summary>Returns a diagnostics snapshot.</summary>
    public PlaybackDiagnostics GetDiagnostics()
    {
        return new PlaybackDiagnostics
        {
            UnderrunCount = Interlocked.Read(ref _underrunCount),
            BufferCapacitySamples = _active?.Ring.Capacity ?? 0,
            BufferedSamples = _active?.Ring.Count ?? 0,
        };
    }

    /// <summary>
    /// Selects an output endpoint. Pass <see langword="null"/> to follow the system default.
    /// Device switches reinitialize WASAPI and may briefly interrupt audible playback; the PCM ring,
    /// DSP configuration, and preloaded next track are preserved when possible.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// The switch was superseded by <see cref="Stop"/>, <see cref="LoadAsync(AudioSource, CancellationToken)"/>, <see cref="Dispose"/>,
    /// another <see cref="SetOutputDeviceAsync"/>, or <paramref name="cancellationToken"/>.
    /// </exception>
    public async Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (deviceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            if (!_outputDevices.TryGetDevice(deviceId, out var device) || device is null)
            {
                throw new AudioDeviceException($"Unknown or unavailable output device id '{deviceId}'.");
            }

            if (device.State is not AudioDeviceState.Active)
            {
                throw new AudioDeviceException(
                    $"Output device '{device.Name}' is not active (state: {device.State}).");
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int switchGeneration;
        CancellationToken switchToken;
        TimeSpan heldPosition;
        var wasPlaying = false;
        var wasPaused = false;
        var hasSource = false;
        var releasedExclusiveDevice = false;
        string? effectiveDeviceId;
        IAudioOutput? oldOutput = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var samePreferred = string.Equals(_preferredOutputDeviceId, deviceId, StringComparison.Ordinal);
            var sameActive = deviceId is null
                ? _preferredOutputDeviceId is null
                  && _outputDeviceStatus is OutputDeviceStatus.Available or OutputDeviceStatus.PreferredUnavailable
                  && _output is not null
                : string.Equals(_activeOutputDeviceId, deviceId, StringComparison.Ordinal)
                  && _outputDeviceStatus is OutputDeviceStatus.Available or OutputDeviceStatus.PreferredUnavailable
                  && _output is not null;

            Volatile.Write(ref _preferredOutputDeviceId, deviceId);

            if (samePreferred && sameActive && !_hasSource)
            {
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
                return;
            }

            if (samePreferred && sameActive && _hasSource && _output is not null)
            {
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
                return;
            }

            effectiveDeviceId = ResolveEffectiveDeviceId_NoLock(deviceId);
            if (deviceId is not null && effectiveDeviceId is null)
            {
                throw new AudioDeviceException($"Output device '{deviceId}' is not available.");
            }

            BeginOutputSwitch_NoLock(cancellationToken, out switchGeneration, out switchToken);

            hasSource = _hasSource;
            if (!hasSource)
            {
                _activeOutputDeviceId = null;
                _outputDeviceStatus = OutputDeviceStatus.Available;
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
                return;
            }

            wasPlaying = _state == PlaybackState.Playing;
            wasPaused = _state == PlaybackState.Paused;
            heldPosition = _state is PlaybackState.Playing
                ? EstimateAudiblePosition()
                : _heldPosition;
            // Snapshot so FinishOutputRebind can prefer _heldPosition (including seek-to-zero).
            _heldPosition = heldPosition;

            releasedExclusiveDevice = _effectiveOutputMode == AudioOutputMode.Exclusive;
            oldOutput = DetachOutput_NoLock();
            _outputDeviceStatus = OutputDeviceStatus.Switching;
            RaiseOutputDeviceChanged(OutputDeviceChangeReason.Switching);
        }
        finally
        {
            _gate.Release();
        }

        // Dedicated-thread Shutdown yields the caller (so Switching observers can proceed) without
        // ForceYield/Task.Run onto a pool that parallel held-shutdown tests can starve.
        await WaitForOutputShutdownAsync().ConfigureAwait(false);

        // Join previous output outside the gate so pull-loop wait cannot deadlock.
        try
        {
            await ShutdownAndDisposeAsync(oldOutput).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Prefer superseded over teardown failure when Stop/Load/Dispose/newer switch won.
            if (_disposed
                || switchGeneration != Volatile.Read(ref _outputSwitchGeneration)
                || switchToken.IsCancellationRequested)
            {
                throw _disposed
                    ? new ObjectDisposedException(nameof(AudioPlayer))
                    : CreateSupersededException();
            }

            await FailSwitchAfterTeardownAsync(
                switchGeneration,
                wasPlaying,
                wasPaused,
                heldPosition,
                ex).ConfigureAwait(false);
            throw;
        }

        ThrowIfSwitchSuperseded(switchGeneration, switchToken);

        if (releasedExclusiveDevice || _outputConfiguration.Mode == AudioOutputMode.Exclusive)
        {
            await Task.Delay(ExclusiveEndpointReleaseGrace, switchToken).ConfigureAwait(false);
            ThrowIfSwitchSuperseded(switchGeneration, switchToken);
        }

        await FinishOutputRebindAsync(
            preferredDeviceId: deviceId,
            effectiveDeviceId,
            switchGeneration,
            switchToken,
            wasPlaying,
            wasPaused,
            heldPosition,
            raiseConfigurationChanged: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies an output-mode configuration (shared/exclusive and fallback policy).
    /// May briefly interrupt audible playback while rebinding the output device.
    /// Preserves the current track, position intent, DSP, and next-track selection when possible.
    /// </summary>
    public async Task ConfigureOutputAsync(
        AudioOutputConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var switchGeneration = 0;
        var switchToken = CancellationToken.None;
        TimeSpan heldPosition = TimeSpan.Zero;
        var wasPlaying = false;
        var wasPaused = false;
        string? preferredDeviceId;
        string? effectiveDeviceId = null;
        IAudioOutput? oldOutput = null;
        var modeUnchanged = false;
        var noSourceConfigured = false;
        var releasedExclusive = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            preferredDeviceId = _preferredOutputDeviceId;
            var previous = _outputConfiguration;
            modeUnchanged = previous.Mode == configuration.Mode
                && previous.ExclusiveFallback == configuration.ExclusiveFallback
                && previous.RequestedLatency == configuration.RequestedLatency
                && _output is not null
                && _effectiveOutputMode == configuration.Mode
                && _outputFallbackReason == ExclusiveFallbackReason.None
                && _outputDeviceStatus is OutputDeviceStatus.Available or OutputDeviceStatus.PreferredUnavailable;

            _outputConfiguration = configuration;
            if (modeUnchanged)
            {
                return;
            }

            effectiveDeviceId = ResolveEffectiveDeviceId_NoLock(preferredDeviceId);
            BeginOutputSwitch_NoLock(cancellationToken, out switchGeneration, out switchToken);
            releasedExclusive = _effectiveOutputMode == AudioOutputMode.Exclusive;

            if (!_hasSource)
            {
                // Preference-only update, but still release any orphaned output (e.g. a disposed
                // zombie left after a failed Load Attach, or a lingering exclusive client).
                oldOutput = DetachOutput_NoLock();
                _outputFallbackReason = ExclusiveFallbackReason.None;
                _outputDeviceStatus = OutputDeviceStatus.Available;
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
                noSourceConfigured = true;
            }
            else
            {
                if (effectiveDeviceId is null && preferredDeviceId is not null)
                {
                    throw new AudioDeviceException($"Output device '{preferredDeviceId}' is not available.");
                }

                wasPlaying = _state == PlaybackState.Playing;
                wasPaused = _state == PlaybackState.Paused;
                heldPosition = _state is PlaybackState.Playing
                    ? EstimateAudiblePosition()
                    : _heldPosition;
                _heldPosition = heldPosition;

                oldOutput = DetachOutput_NoLock();
                _effectiveOutputMode = null;
                _outputDeviceStatus = OutputDeviceStatus.Switching;
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Switching);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (modeUnchanged)
        {
            RaiseOutputConfigurationChanged();
            return;
        }

        await WaitForOutputShutdownAsync().ConfigureAwait(false);

        try
        {
            await ShutdownAndDisposeAsync(oldOutput).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (noSourceConfigured)
            {
                // Best-effort preference update; orphan dispose failure must not block mode save.
                RaiseError(ex);
            }
            else if (_disposed
                || switchGeneration != Volatile.Read(ref _outputSwitchGeneration)
                || switchToken.IsCancellationRequested)
            {
                throw _disposed
                    ? new ObjectDisposedException(nameof(AudioPlayer))
                    : CreateSupersededException();
            }
            else
            {
                await FailSwitchAfterTeardownAsync(
                    switchGeneration,
                    wasPlaying,
                    wasPaused,
                    heldPosition,
                    ex).ConfigureAwait(false);
                RaiseOutputConfigurationChanged();
                throw;
            }
        }

        if (noSourceConfigured)
        {
            if (releasedExclusive)
            {
                await Task.Delay(ExclusiveEndpointReleaseGrace, CancellationToken.None).ConfigureAwait(false);
            }

            RaiseOutputConfigurationChanged();
            return;
        }

        ThrowIfSwitchSuperseded(switchGeneration, switchToken);

        // WASAPI: opening a new client immediately after disposing the previous one (especially
        // exclusive) can fail with DEVICE_IN_USE or native-crash in GetBuffer on some drivers.
        if (releasedExclusive || configuration.Mode == AudioOutputMode.Exclusive)
        {
            await Task.Delay(ExclusiveEndpointReleaseGrace, switchToken).ConfigureAwait(false);
            ThrowIfSwitchSuperseded(switchGeneration, switchToken);
        }

        await FinishOutputRebindAsync(
            preferredDeviceId,
            effectiveDeviceId,
            switchGeneration,
            switchToken,
            wasPlaying,
            wasPaused,
            heldPosition,
            raiseConfigurationChanged: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads an audio file, releasing any previously loaded source and clearing any preloaded next track.
    /// Locks the session output format to this track for subsequent gapless joins.
    /// </summary>
    public Task LoadAsync(string filePath, CancellationToken cancellationToken = default) =>
        LoadAsync(FileAudioSource.Create(filePath), cancellationToken);

    /// <summary>
    /// Loads an audio source, releasing any previously loaded source and clearing any preloaded next track.
    /// Locks the session output format to this track for subsequent gapless joins.
    /// </summary>
    public async Task LoadAsync(AudioSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var generation = 0;
        SessionTeardownBag? priorSession = null;
        var releasedExclusive = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_generation;
            CancelOutputSwitch_NoLock();
            _outputRecoveryIntent = OutputRecoveryIntent.None;
            SetState_NoLock(PlaybackState.Buffering);

            releasedExclusive = _effectiveOutputMode == AudioOutputMode.Exclusive;
            priorSession = BeginTearDownSession_NoLock(takeNext: true);
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        if (priorSession is not null)
        {
            await CompleteTeardownAsync(priorSession).ConfigureAwait(false);
        }

        // Wait for any fire-and-forget exclusive dispose (device-loss path) before opening again.
        await WaitForOutputShutdownAsync().ConfigureAwait(false);
        if (releasedExclusive || _outputConfiguration.Mode == AudioOutputMode.Exclusive)
        {
            await Task.Delay(ExclusiveEndpointReleaseGrace, cancellationToken).ConfigureAwait(false);
        }

        IAudioDecoder decoder;
        try
        {
            decoder = await _decoderFactory.CreateAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (generation == _generation && !_disposed)
                {
                    SetState_NoLock(PlaybackState.Stopped);
                }
            }
            finally
            {
                _gate.Release();
            }

            throw;
        }

        // Capture before any format conversion wraps the decoder (initial load typically does not wrap).
        var networkInfo = decoder as INetworkTransportInfo;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? preferred;
        string? effectiveId;
        int switchGeneration;
        CancellationToken switchToken;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (generation != _generation)
            {
                decoder.Dispose();
                return;
            }

            try
            {
                PrepareSessionPipelines_NoLock(source, decoder, networkInfo);
            }
            catch
            {
                decoder.Dispose();
                SetState_NoLock(PlaybackState.Stopped);
                throw;
            }

            preferred = _preferredOutputDeviceId;
            effectiveId = ResolveEffectiveDeviceId_NoLock(preferred);
            BeginOutputSwitch_NoLock(cancellationToken, out switchGeneration, out switchToken);
        }
        finally
        {
            _gate.Release();
        }

        IAudioOutput? created = null;
        try
        {
            created = await _outputFactory
                .CreateOutputAsync(BuildCreateRequest(effectiveId, _outputConfiguration.Mode), switchToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SessionTeardownBag? failedOpenTeardown = null;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (generation == _generation && !_disposed)
                {
                    failedOpenTeardown = BeginTearDownSession_NoLock(takeNext: true);
                    _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                    _effectiveOutputMode = null;
                    SetState_NoLock(PlaybackState.Stopped);
                    RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
                }
            }
            finally
            {
                _gate.Release();
            }

            if (failedOpenTeardown is not null)
            {
                await CompleteTeardownAsync(failedOpenTeardown).ConfigureAwait(false);
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw ex is AudioException
                ? ex
                : new AudioOutputException("Failed to open the output device.", ex);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (generation != _generation || switchGeneration != _outputSwitchGeneration)
            {
                created.Dispose();
                return;
            }

            if (_sampleSource is null)
            {
                created.Dispose();
                SetState_NoLock(PlaybackState.Stopped);
                return;
            }

            var loadMode = _outputConfiguration.Mode;
            try
            {
                AttachOutput_NoLock(created, TimeSpan.Zero, loadMode);
                created = null;
            }
            catch (Exception attachEx) when (
                loadMode == AudioOutputMode.Exclusive
                && _outputConfiguration.ExclusiveFallback == ExclusiveModeFallback.Shared
                && OutputFailureHelpers.IsFallbackEligible(OutputFailureHelpers.KindOf(attachEx)))
            {
                ShutdownAndDispose(created);
                created = null;
                _outputFallbackReason = OutputFailureHelpers.ToFallbackReason(
                    OutputFailureHelpers.KindOf(attachEx));
                _gate.Release();
                try
                {
                    created = await _outputFactory
                        .CreateOutputAsync(BuildCreateRequest(effectiveId, AudioOutputMode.Shared), switchToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (generation != _generation || switchGeneration != _outputSwitchGeneration || _sampleSource is null)
                {
                    created?.Dispose();
                    return;
                }

                AttachOutput_NoLock(created!, TimeSpan.Zero, AudioOutputMode.Shared);
                created = null;
                loadMode = AudioOutputMode.Shared;
            }

            _hasSource = true;
            EnsureActivePumpRunning_NoLock();
            _active!.Prime(_options.PrimeDuration);

            if (preferred is not null
                && !string.Equals(_activeOutputDeviceId, preferred, StringComparison.Ordinal))
            {
                _outputDeviceStatus = OutputDeviceStatus.PreferredUnavailable;
            }
            else
            {
                _outputDeviceStatus = OutputDeviceStatus.Available;
            }

            SetState_NoLock(PlaybackState.Stopped);
            RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
            if (loadMode != _outputConfiguration.Mode
                || _outputConfiguration.Mode == AudioOutputMode.Exclusive)
            {
                // Raise after gate release.
            }
        }
        finally
        {
            if (created is not null)
            {
                try
                {
                    created.Dispose();
                }
                catch
                {
                    // ignored
                }

                created = null;
            }

            // Attach/Init failure (or superseded create) can leave pipelines without an output.
            // Tear them down so Seek cannot observe a half-loaded session / disposed output.
            if (!_disposed
                && generation == _generation
                && !_hasSource
                && _active is not null)
            {
                var failedLoad = BeginTearDownSession_NoLock(takeNext: true);
                _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                SetState_NoLock(PlaybackState.Stopped);
                try
                {
                    CompleteTeardown(failedLoad);
                }
                catch
                {
                    // ignored — original attach/load exception is authoritative
                }
            }

            _gate.Release();
        }

        RaiseOutputConfigurationChanged();
    }

    /// <summary>
    /// Preloads exactly one upcoming track without interrupting current playback.
    /// Replaces any previous preload. Failures set <see cref="NextTrackState.Failed"/> and do not stop the current track.
    /// Decoded PCM is converted to the session format and buffered with a bounded ring.
    /// </summary>
    public Task PreloadNextAsync(string filePath, CancellationToken cancellationToken = default) =>
        PreloadNextAsync(FileAudioSource.Create(filePath), cancellationToken);

    /// <summary>
    /// Preloads exactly one upcoming track without interrupting current playback.
    /// Replaces any previous preload. Failures set <see cref="NextTrackState.Failed"/> and do not stop the current track.
    /// Decoded PCM is converted to the session format and buffered with a bounded ring.
    /// </summary>
    public async Task PreloadNextAsync(AudioSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int preloadGeneration;
        AudioFormat sessionFormat;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hasSource)
            {
                throw new InvalidOperationException("Load a source before preloading the next track.");
            }

            sessionFormat = _sessionFormat;
            preloadGeneration = _next.BeginLoad(source);
        }
        finally
        {
            _gate.Release();
        }

        IAudioDecoder? opened = null;
        TrackDecodePipeline? pipeline = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            opened = await _decoderFactory.CreateAsync(source, cancellationToken).ConfigureAwait(false);
            if (!_next.IsCurrentGeneration(preloadGeneration))
            {
                opened.Dispose();
                return;
            }

            // Capture before Convert may wrap (ChannelMixing / WDL).
            var networkInfo = opened as INetworkTransportInfo;
            var converted = _formatConverter.Convert(opened, sessionFormat);
            opened = null; // ownership transferred to converted (or same instance)
            pipeline = TrackDecodePipeline.Create(source, converted, _options, networkInfo);
            pipeline.StartPump(
                _options.Processors,
                onSamplesWritten: _ => { },
                onError: ex =>
                {
                    if (_next.TryCompleteFailed(preloadGeneration, ex))
                    {
                        RaiseError(ex);
                    }
                });
            pipeline.Prime(_options.PrimeDuration);

            if (!_next.TryCompleteReady(preloadGeneration, pipeline))
            {
                return;
            }

            pipeline = null;
        }
        catch (OperationCanceledException)
        {
            pipeline?.Dispose();
            opened?.Dispose();
            if (_next.IsCurrentGeneration(preloadGeneration))
            {
                _next.Clear();
            }

            throw;
        }
        catch (Exception ex)
        {
            pipeline?.Dispose();
            opened?.Dispose();
            if (_next.TryCompleteFailed(preloadGeneration, ex))
            {
                RaiseError(ex);
            }
        }
    }

    /// <summary>
    /// Cancels and disposes any pending or ready next track.
    /// </summary>
    public void ClearNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _next.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts or resumes playback.</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hasSource || _active is null)
            {
                throw new InvalidOperationException("No audio source is loaded.");
            }

            if (_output is null
                || _outputDeviceStatus is OutputDeviceStatus.Unavailable or OutputDeviceStatus.Switching)
            {
                _outputRecoveryIntent = OutputRecoveryIntent.ResumePlaying;
                throw new InvalidOperationException("No usable audio output device is available.");
            }

            if (_state == PlaybackState.Playing)
            {
                return;
            }

            // Exclusive WASAPI Pause stops the device client; on resume the device clock restarts
            // near zero. Re-anchor to the held logical position before starting the output again.
            if (_state == PlaybackState.Paused)
            {
                RebasePositionClock_NoLock(_heldPosition);
            }

            EnsureActivePumpRunning_NoLock();
            _active.Prime(_options.PrimeDuration);
            _completionRaised = false;
            Volatile.Write(ref _acceptEndOfStreamCompletion, 1);
            _outputRecoveryIntent = OutputRecoveryIntent.None;
            SetState_NoLock(PlaybackState.Playing);
            _output.Play();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pauses playback and preserves position.</summary>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not PlaybackState.Playing)
            {
                return;
            }

            _heldPosition = EstimateAudiblePosition();
            RebasePositionClock_NoLock(_heldPosition);
            _output?.Pause();
            SetState_NoLock(PlaybackState.Paused);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops playback, resets position to the beginning, and clears any preloaded next track
    /// without raising <see cref="PlaybackCompleted"/>.
    /// Also clears <see cref="OutputRecoveryIntent"/> so later device recovery will not resume.
    /// </summary>
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TrackDecodePipeline? decoderReset = null;
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelOutputSwitch_NoLock();
            _outputRecoveryIntent = OutputRecoveryIntent.None;
            _next.Clear();
            if (!_hasSource)
            {
                if (_outputDeviceStatus == OutputDeviceStatus.Switching)
                {
                    _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                }

                SetState_NoLock(PlaybackState.Stopped);
                return;
            }

            // Invalidate in-flight Seek apply so it cannot resume playback after Stop.
            _activeTrackGeneration++;
            decoderReset = StopTransport_NoLock(resetToStart: true, raiseCompletion: false);
            if (_output is null)
            {
                _outputDeviceStatus = OutputDeviceStatus.Unavailable;
            }
        }
        finally
        {
            _gate.Release();
        }

        // Decoder.Seek must not run under _gate (same AppHang class as user Seek).
        if (decoderReset is not null)
        {
            ApplyDecoderResetToStart(decoderReset);
        }
    }

    /// <summary>
    /// Seeks within the active track. Preserves a safe preloaded next track; does not trigger a transition.
    /// </summary>
    public void Seek(TimeSpan position) => _ = SeekWithResult(position);

    /// <summary>
    /// Seeks within the active track and returns requested vs actual decoded landing information.
    /// Preserves a safe preloaded next track; does not trigger a transition.
    /// Timestamps in the result are source-timeline positions (frame-rounded).
    /// </summary>
    /// <remarks>
    /// Pump stop, decoder seek, and ring priming run <b>off</b> the command gate so a slow Media Foundation
    /// seek (or a held decode worker) cannot freeze UI/dispose. Callers on a UI thread should still prefer
    /// invoking this off the UI thread when possible.
    /// </remarks>
    public AudioSeekResult SeekWithResult(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);

        TrackDecodePipeline active;
        TransitionSampleSource? sampleSource;
        var wasPlaying = false;
        var wasPaused = false;
        var previousHeld = TimeSpan.Zero;
        var seekGeneration = 0;

        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_pipelineSync)
            {
                if (_active is null)
                {
                    throw new InvalidOperationException("No audio source is loaded.");
                }

                active = _active;
            }

            if (!active.Decoder.CanSeek)
            {
                throw new NotSupportedException("The current audio source does not support seeking.");
            }

            if (_hasKnownDuration && position > _duration && _duration > TimeSpan.Zero)
            {
                position = _duration;
            }

            // Invalidate in-flight transition notifications from a pre-seek drain.
            seekGeneration = ++_activeTrackGeneration;
            _suppressTransitionIdsAtOrBelow = Volatile.Read(ref _transitionId);

            wasPlaying = _state == PlaybackState.Playing;
            wasPaused = _state == PlaybackState.Paused;
            previousHeld = _heldPosition;
            sampleSource = _sampleSource;

            try
            {
                _output?.Pause();
            }
            catch (ObjectDisposedException)
            {
                // Failed Load can leave a disposed output reference in rare races; seek must still work.
            }
            catch (Exception)
            {
                // Continue — barrier still protects DSP reset.
            }

            // Silence the pull path before releasing the gate so DSP reset cannot race callbacks.
            sampleSource?.EnterEffectsBarrier(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _gate.Release();
        }

        AudioSeekResult result;
        try
        {
            // StopPump / Decoder.Seek / Clear must not run under _gate (see TrackDecodePipeline.StopPump).
            // Media Foundation seeks can block for a long time; holding the gate here AppHangs the UI and
            // prevents Dispose from completing.
            active.StopPump();
            active.Ring.Clear();
            sampleSource?.ResetEndOfStream();

            var duration = active.Duration > TimeSpan.Zero ? active.Duration : TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration)
            {
                position = duration;
            }

            _decoderSeekGate.Wait();
            try
            {
                result = active.Decoder.Seek(position);
            }
            finally
            {
                _decoderSeekGate.Release();
            }
        }
        catch
        {
            _gate.Wait();
            try
            {
                RestoreAfterFailedSeek_NoLock(
                    active,
                    sampleSource,
                    seekGeneration,
                    wasPlaying,
                    wasPaused,
                    previousHeld);
            }
            finally
            {
                _gate.Release();
            }

            throw;
        }

        // Prime off-gate: may sleep up to PrimeDuration while the pump fills the ring.
        if (wasPlaying)
        {
            try
            {
                _gate.Wait();
                try
                {
                    ThrowIfSeekSuperseded_NoLock(active, seekGeneration);
                    EnsureActivePumpRunning_NoLock();
                }
                finally
                {
                    _gate.Release();
                }

                active.Prime(_options.PrimeDuration);
            }
            catch
            {
                _gate.Wait();
                try
                {
                    sampleSource?.ExitEffectsBarrier();
                }
                finally
                {
                    _gate.Release();
                }

                throw;
            }
        }

        _gate.Wait();
        try
        {
            if (_disposed
                || seekGeneration != _activeTrackGeneration
                || !ReferenceEquals(_active, active))
            {
                sampleSource?.ExitEffectsBarrier();
                throw new InvalidOperationException("Seek was superseded by another session operation.");
            }

            try
            {
                _effects.ResetProcessingState();
                _effects.DisposeRetired();

                // Authoritative timeline from evidence-backed actual when present; otherwise requested intent.
                var timelinePosition = result.ActualPosition ?? result.RequestedPosition;
                _timelineOriginSamples = active.Format.TimeToSamples(timelinePosition);
                Interlocked.Exchange(ref _samplesDecoded, 0);
                Interlocked.Exchange(ref _samplesConsumed, 0);
                _completionRaised = false;
                Interlocked.Exchange(ref _underrunLatched, 0);
                // Allow natural completion again after seeking away from a prior EOS.
                Volatile.Write(ref _acceptEndOfStreamCompletion, 1);
                RebasePositionClock_NoLock(timelinePosition);

                EnsureActivePumpRunning_NoLock();

                // Pause may have landed while Decoder.Seek ran off-gate — do not resume.
                // Stop bumps _activeTrackGeneration and is rejected by the supersede check above.
                var resumePlaying = wasPlaying
                    && _state != PlaybackState.Paused
                    && _output is not null;
                if (resumePlaying)
                {
                    _output!.Play();
                    SetState_NoLock(PlaybackState.Playing);
                }
                else if (wasPaused || _state == PlaybackState.Paused)
                {
                    SetState_NoLock(PlaybackState.Paused);
                }
            }
            finally
            {
                sampleSource?.ExitEffectsBarrier();
            }

            return result.EnsureInvariants();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfSeekSuperseded_NoLock(TrackDecodePipeline active, int seekGeneration)
    {
        if (_disposed
            || seekGeneration != _activeTrackGeneration
            || !ReferenceEquals(_active, active))
        {
            throw new InvalidOperationException("Seek was superseded by another session operation.");
        }
    }

    /// <summary>
    /// Rewinds the decoder to the start off the command gate, then applies timeline under the gate
    /// if <paramref name="active"/> is still the current pipeline.
    /// </summary>
    private void ApplyDecoderResetToStart(TrackDecodePipeline active)
    {
        AudioSeekResult? seekResult = null;
        try
        {
            if (active.Decoder.CanSeek)
            {
                _decoderSeekGate.Wait();
                try
                {
                    seekResult = active.Decoder.Seek(TimeSpan.Zero);
                }
                finally
                {
                    _decoderSeekGate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DecoderException or NotSupportedException)
        {
            // Concurrent teardown or unsupported seek — timeline stays at the StopTransport zero intent.
        }

        _gate.Wait();
        try
        {
            if (_disposed || !ReferenceEquals(_active, active))
            {
                return;
            }

            var origin = seekResult?.ActualPosition ?? TimeSpan.Zero;
            _timelineOriginSamples = active.Format.TimeToSamples(origin);
            Interlocked.Exchange(ref _samplesDecoded, 0);
            Interlocked.Exchange(ref _samplesConsumed, 0);
            RebasePositionClock_NoLock(TimeSpan.Zero);
            if (_state == PlaybackState.Stopped)
            {
                EnsureActivePumpRunning_NoLock();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RestoreAfterFailedSeek_NoLock(
        TrackDecodePipeline attempted,
        TransitionSampleSource? sampleSource,
        int seekGeneration,
        bool wasPlaying,
        bool wasPaused,
        TimeSpan previousHeld)
    {
        try
        {
            if (!_disposed
                && seekGeneration == _activeTrackGeneration
                && ReferenceEquals(_active, attempted))
            {
                attempted.Ring.Clear();
                sampleSource?.ResetEndOfStream();
                EnsureActivePumpRunning_NoLock();
                RebasePositionClock_NoLock(previousHeld);
                if (wasPlaying && _output is not null)
                {
                    _output.Play();
                    SetState_NoLock(PlaybackState.Playing);
                }
                else if (wasPaused)
                {
                    SetState_NoLock(PlaybackState.Paused);
                }
                else
                {
                    SetState_NoLock(PlaybackState.Stopped);
                }
            }
        }
        catch
        {
            // Preserve the original seek failure.
        }
        finally
        {
            sampleSource?.ExitEffectsBarrier();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Detaches session resources under the command gate, then completes output shutdown and pump stop
    /// after releasing the gate. Concurrent callers share and await the same teardown completion.
    /// May still block off-gate while waiting for output quiescence or the decode worker.
    /// Prefer <see cref="DisposeAsync"/> from asynchronous code.
    /// </remarks>
    public void Dispose()
    {
        var tcs = BeginDisposeShared(out var owns);
        if (!owns)
        {
            tcs.Task.GetAwaiter().GetResult();
            return;
        }

        try
        {
            SessionTeardownBag? bag = null;
            _gate.Wait();
            try
            {
                bag = BeginDispose_NoLock();
            }
            finally
            {
                _gate.Release();
            }

            if (bag is not null)
            {
                CompleteTeardown(bag);
            }

            NotifyDisposed();
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            NotifyDisposed();
            tcs.TrySetException(ex);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Same detach-under-gate / complete-off-gate contract as <see cref="Dispose"/>, but awaits
    /// decode-pump stop asynchronously. Concurrent callers await the same shared completion.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var tcs = BeginDisposeShared(out var owns);
        if (!owns)
        {
            await tcs.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            SessionTeardownBag? bag = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                bag = BeginDispose_NoLock();
            }
            finally
            {
                _gate.Release();
            }

            if (bag is not null)
            {
                await CompleteTeardownAsync(bag).ConfigureAwait(false);
            }

            NotifyDisposed();
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            NotifyDisposed();
            tcs.TrySetException(ex);
            throw;
        }
    }

    private TaskCompletionSource BeginDisposeShared(out bool owns)
    {
        lock (_disposeSync)
        {
            if (_disposeTcs is not null)
            {
                owns = false;
                return _disposeTcs;
            }

            _disposeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owns = true;
            return _disposeTcs;
        }
    }

    private SessionTeardownBag BeginDispose_NoLock()
    {
        _disposed = true;
        _generation++;
        CancelOutputSwitch_NoLock();
        _outputRecoveryIntent = OutputRecoveryIntent.None;
        _outputDevices.DevicesChanged -= OnDevicesChanged;
        _outputDevices.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        var bag = BeginTearDownSession_NoLock(takeNext: true);
        _effects.Dispose();
        SetState_NoLock(PlaybackState.Stopped);
        return bag;
    }

    private void NotifyDisposed()
    {
        try
        {
            _onDisposed?.Invoke();
        }
        catch
        {
            // ignored — ownership bookkeeping must not throw from Dispose
        }
    }

    private void PrepareSessionPipelines_NoLock(
        AudioSource source,
        IAudioDecoder decoder,
        INetworkTransportInfo? networkInfo = null)
    {
        _sessionFormat = decoder.Format;
        _currentSource = source;
        Volatile.Write(ref _sourcePath, source.Identifier);
        _duration = decoder.Duration;
        _hasKnownDuration = decoder.HasKnownDuration;
        _timelineOriginSamples = 0;
        Interlocked.Exchange(ref _samplesDecoded, 0);
        Interlocked.Exchange(ref _samplesConsumed, 0);
        _completionRaised = false;
        Interlocked.Exchange(ref _underrunCount, 0);
        Interlocked.Exchange(ref _underrunLatched, 0);
        Volatile.Write(ref _positionFrozen, 0);
        _logicalPositionAtAnchor = TimeSpan.Zero;
        _deviceClockAnchor = TimeSpan.Zero;
        _heldPosition = TimeSpan.Zero;
        _activeTrackGeneration++;
        _suppressTransitionIdsAtOrBelow = _transitionId;

        networkInfo ??= decoder as INetworkTransportInfo;
        BindNetworkInfo_NoLock(networkInfo);

        _active = TrackDecodePipeline.Create(source, decoder, _options, networkInfo);

        _effects.Configure(decoder.Format);
        _effects.ResetProcessingState();
        _effects.DisposeRetired();

        _sampleSource = CreateTransitionSampleSource_NoLock(decoder.Format);
    }

    private IAudioOutput? DetachOutput_NoLock()
    {
        Volatile.Write(ref _acceptEndOfStreamCompletion, 0);
        var output = _output;
        if (output is null)
        {
            return null;
        }

        try
        {
            output.Stopped -= OnOutputStopped;
        }
        catch
        {
            // ignored
        }

        // Do not Pause/Stop/Join under the command gate — Fake/WASAPI Pause joins the pull
        // loop and can deadlock with callback work that needs the gate. Quiescence is
        // ShutdownAndDispose after the gate is released (see BeginTearDownSession docs).
        _output = null;
        _effectiveOutputMode = null;
        Volatile.Write(ref _activeOutputDeviceId, null);
        return output;
    }

    /// <summary>
    /// Guarantees the output will not read PCM again, then disposes it.
    /// Throws if quiescence cannot be guaranteed; Dispose is still attempted.
    /// </summary>
    /// <remarks>
    /// When a UI <see cref="SynchronizationContext"/> is present, teardown hops to a dedicated
    /// thread so WASAPI <c>Stop</c>/<c>Join</c> cannot AppHang the UI. Without a sync context the
    /// caller already runs off-UI (Dispose after gate release, queued teardown thread, etc.) and
    /// executes Shutdown inline. Prefer <see cref="ShutdownAndDisposeAsync"/> from async rebind
    /// paths so the caller yields before a held Shutdown without <c>Task.Run</c>/<c>ForceYield</c>
    /// pool starvation.
    /// </remarks>
    private static void ShutdownAndDispose(IAudioOutput? output)
    {
        if (output is null)
        {
            return;
        }

        if (SynchronizationContext.Current is null)
        {
            ShutdownAndDisposeCore(output);
            return;
        }

        ShutdownAndDisposeAsync(output).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async variant of <see cref="ShutdownAndDispose"/>: starts teardown on a dedicated thread and
    /// returns a task that completes when Shutdown/Dispose finish (or fail).
    /// </summary>
    private static Task ShutdownAndDisposeAsync(IAudioOutput? output)
    {
        if (output is null)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ShutdownAndDisposeCore(output);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Sonora.OutputShutdown",
        };
        thread.Start();
        return tcs.Task;
    }

    private static void ShutdownAndDisposeCore(IAudioOutput output)
    {
        Exception? shutdownError = null;
        try
        {
            output.Shutdown();
        }
        catch (Exception ex)
        {
            shutdownError = ex is AudioOutputException
                ? ex
                : new AudioOutputException(
                    "Output shutdown failed; replacement output was not started.",
                    ex);
        }

        try
        {
            output.Dispose();
        }
        catch
        {
            // ignored — shutdown error (if any) is authoritative
        }

        if (shutdownError is not null)
        {
            throw shutdownError;
        }
    }

    /// <summary>
    /// Queues Shutdown/Dispose off the command gate and serializes with other pending teardowns.
    /// Uses a dedicated thread (not the thread pool) so a held Shutdown cannot starve other work.
    /// </summary>
    private void QueueOutputShutdown(IAudioOutput? output)
    {
        if (output is null)
        {
            return;
        }

        lock (_outputShutdownSync)
        {
            var previous = _outputShutdownTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _outputShutdownTask = tcs.Task;
            var thread = new Thread(() =>
            {
                try
                {
                    try
                    {
                        previous.GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // ignored — prior shutdown failure must not block releasing this client
                    }

                    try
                    {
                        ShutdownAndDispose(output);
                    }
                    catch (Exception ex)
                    {
                        RaiseError(ex);
                    }
                }
                finally
                {
                    tcs.TrySetResult();
                }
            })
            {
                IsBackground = true,
                Name = "Sonora.QueuedOutputShutdown",
            };
            thread.Start();
        }
    }

    private Task WaitForOutputShutdownAsync()
    {
        lock (_outputShutdownSync)
        {
            return _outputShutdownTask;
        }
    }

    private void ThrowIfSwitchSuperseded(int switchGeneration, CancellationToken switchToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioPlayer));
        }

        if (switchGeneration != Volatile.Read(ref _outputSwitchGeneration) || switchToken.IsCancellationRequested)
        {
            throw CreateSupersededException();
        }
    }

    private static OperationCanceledException CreateSupersededException() =>
        new("The output device switch was superseded by another operation.");

    private async Task FailSwitchAfterTeardownAsync(
        int switchGeneration,
        bool wasPlaying,
        bool wasPaused,
        TimeSpan heldPosition,
        Exception error)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed || switchGeneration != _outputSwitchGeneration)
            {
                throw CreateSupersededException();
            }

            _activeOutputDeviceId = null;
            _effectiveOutputMode = null;
            _outputDeviceStatus = OutputDeviceStatus.Unavailable;
            _heldPosition = heldPosition;
            if (wasPlaying)
            {
                _outputRecoveryIntent = OutputRecoveryIntent.ResumePlaying;
            }
            else if (wasPaused)
            {
                _outputRecoveryIntent = OutputRecoveryIntent.RemainPaused;
            }

            var kind = OutputFailureHelpers.KindOf(error);
            if (kind == AudioOutputFailureKind.Unspecified)
            {
                // Walk inners (e.g. factory failure wrapped by teardown).
                for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
                {
                    kind = OutputFailureHelpers.KindOf(inner);
                    if (kind != AudioOutputFailureKind.Unspecified)
                    {
                        break;
                    }
                }
            }

            if (kind != AudioOutputFailureKind.Unspecified
                && kind != AudioOutputFailureKind.ShutdownFailed)
            {
                _outputFallbackReason = OutputFailureHelpers.ToFallbackReason(kind);
            }
            else if (_outputConfiguration.Mode == AudioOutputMode.Exclusive
                     && kind == AudioOutputFailureKind.ShutdownFailed)
            {
                // Quiesce failure during an exclusive rebind still leaves exclusive preferred;
                // surface a classified reason so apps can retry / decide on fallback.
                _outputFallbackReason = ExclusiveFallbackReason.InitializationFailed;
            }

            // Keep _hasSource, DSP, next-track selection, and recovery intent so apps can retry.
            SetState_NoLock(PlaybackState.Stopped);
            RaiseError(error);
            RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AttachOutput_NoLock(IAudioOutput output, TimeSpan logicalPosition, AudioOutputMode effectiveMode)
    {
        _effects.ResetProcessingState();
        _effects.DisposeRetired();
        _sampleSource?.ResetEndOfStream();

        // Initialize before publishing _output. If Init throws (e.g. exclusive DEVICE_IN_USE),
        // callers dispose the instance — leaving _output assigned would make later Seek/Pause
        // hit ObjectDisposedException and leave the endpoint locked from the app's perspective.
        output.Stopped += OnOutputStopped;
        try
        {
            output.Initialize(_sampleSource!);
        }
        catch
        {
            try
            {
                output.Stopped -= OnOutputStopped;
            }
            catch
            {
                // ignored
            }

            throw;
        }

        _output = output;
        Volatile.Write(ref _activeOutputDeviceId, output.EndpointId);
        _effectiveOutputMode = effectiveMode;
        RebasePositionClock_NoLock(logicalPosition);
    }

    private string? ResolveEffectiveDeviceId_NoLock(string? preferredId)
    {
        if (preferredId is not null)
        {
            if (_outputDevices.TryGetDevice(preferredId, out var device)
                && device is not null
                && device.State == AudioDeviceState.Active)
            {
                return preferredId;
            }

            // Fall back to system default while keeping the preferred id.
            return _outputDevices.GetDefaultDevice() is { State: AudioDeviceState.Active } def
                ? def.Id
                : null;
        }

        return _outputDevices.GetDefaultDevice() is { State: AudioDeviceState.Active } d
            ? d.Id
            : null;
    }

    private TransitionSampleSource.PromoteResult? TryPromoteNextFromCallback()
    {
        // Called from the audio pull path — TryEnter only; never block waiting on Seek/Load.
        if (!Monitor.TryEnter(_pipelineSync))
        {
            return null;
        }

        TrackDecodePipeline? previous = null;
        string? previousPath = null;
        var previousDuration = TimeSpan.Zero;
        string? nextPath = null;
        TrackDecodePipeline? nextPipeline = null;
        INetworkTransportInfo? previousNetwork = null;
        INetworkTransportInfo? nextNetwork = null;
        var transitionId = 0;
        try
        {
            if (!_next.TryTakeReady(out nextPipeline, out nextPath))
            {
                return null;
            }

            previous = _active;
            previousPath = previous?.Path;
            previousDuration = previous?.Duration ?? TimeSpan.Zero;
            _active = nextPipeline;
            _currentSource = nextPipeline.Source;
            Volatile.Write(ref _sourcePath, nextPath);
            _duration = nextPipeline.Duration;
            _hasKnownDuration = nextPipeline.Decoder.HasKnownDuration;
            _timelineOriginSamples = 0;
            Interlocked.Exchange(ref _samplesDecoded, 0);
            Interlocked.Exchange(ref _samplesConsumed, 0);
            Interlocked.Exchange(ref _underrunLatched, 0);
            Volatile.Write(ref _positionFrozen, 0);

            // Rebase logical timeline to the start of B using the continuous device clock.
            _logicalPositionAtAnchor = TimeSpan.Zero;
            _deviceClockAnchor = _output?.TryGetRenderedPosition() ?? TimeSpan.Zero;
            _heldPosition = TimeSpan.Zero;

            // Swap network probe under lock; subscribe/unsubscribe off the audio callback below.
            previousNetwork = _networkInfo;
            nextNetwork = nextPipeline.NetworkInfo ?? nextPipeline.Decoder as INetworkTransportInfo;
            _networkInfo = nextNetwork;
            if (nextNetwork is not null)
            {
                _networkStatus = nextNetwork.TransportStatus;
                Volatile.Write(ref _networkBytesBuffered, nextNetwork.NetworkBytesBuffered);
            }
            else
            {
                _networkStatus = NetworkTransportStatus.Idle;
                Volatile.Write(ref _networkBytesBuffered, 0);
            }

            transitionId = Interlocked.Increment(ref _transitionId);
        }
        finally
        {
            Monitor.Exit(_pipelineSync);
        }

        var args = new TrackTransitionedEventArgs(previousPath, nextPath, previousDuration, transitionId);
        var toDispose = previous;
        var oldNetwork = previousNetwork;
        var newNetwork = nextNetwork;
        _ = Task.Run(() =>
        {
            try
            {
                if (oldNetwork is not null)
                {
                    oldNetwork.TransportChanged -= OnNetworkTransportChanged;
                }

                if (newNetwork is not null)
                {
                    newNetwork.TransportChanged += OnNetworkTransportChanged;
                }

                try
                {
                    NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // ignored
                }

                toDispose?.Dispose();
            }
            catch
            {
                // ignored
            }
        });

        return new TransitionSampleSource.PromoteResult(args);
    }

    private void ScheduleTrackTransitioned(TrackTransitionedEventArgs args)
    {
        // Never invoke user handlers on the audio callback thread.
        var capturedSession = Volatile.Read(ref _generation);
        _ = Task.Run(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (capturedSession != Volatile.Read(ref _generation))
            {
                return;
            }

            if (args.TransitionId <= Volatile.Read(ref _suppressTransitionIdsAtOrBelow))
            {
                return;
            }

            try
            {
                TrackTransitioned?.Invoke(this, args);
            }
            catch
            {
                // ignored
            }
        });
    }

    private void EnsureActivePumpRunning_NoLock()
    {
        var active = _active;
        if (active is null)
        {
            return;
        }

        active.StartPump(
            _options.Processors,
            onSamplesWritten: n => Interlocked.Add(ref _samplesDecoded, n),
            onError: ex => RaiseError(ex));
    }

    /// <summary>
    /// Detaches session resources under the command gate without waiting for output quiescence or pump stop.
    /// Callers must complete teardown with <see cref="CompleteTeardown"/> / <see cref="CompleteTeardownAsync"/>
    /// after releasing the gate.
    /// </summary>
    private SessionTeardownBag BeginTearDownSession_NoLock(bool takeNext)
    {
        Volatile.Write(ref _acceptEndOfStreamCompletion, 0);

        try
        {
            _sessionCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        var bag = new SessionTeardownBag
        {
            Output = DetachOutput_NoLock(),
            Active = _active,
            SessionCts = _sessionCts,
            Next = takeNext ? _next.TakeAndClear() : null,
        };

        _active = null;
        _sampleSource = null;
        _sessionCts = null;

        _effects.DisposeRetired();

        _hasSource = false;
        _currentSource = null;
        Volatile.Write(ref _sourcePath, null);
        _duration = TimeSpan.Zero;
        _hasKnownDuration = false;
        _heldPosition = TimeSpan.Zero;
        Volatile.Write(ref _positionFrozen, 0);
        UnbindNetworkInfo_NoLock(setIdle: true);
        return bag;
    }

    /// <summary>
    /// Completes teardown off the command gate. Guarantees the detached output is shut down before dispose
    /// so a failed shutdown cannot leave a second active PCM consumer. Also awaits any queued
    /// device-loss shutdown so Dispose does not return while an orphaned teardown is still running.
    /// </summary>
    private void CompleteTeardown(SessionTeardownBag bag)
    {
        try
        {
            WaitForOutputShutdownAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // prior queued shutdown failure must not block releasing this session
        }

        try
        {
            ShutdownAndDispose(bag.Output);
        }
        catch
        {
            try
            {
                bag.Output?.Dispose();
            }
            catch
            {
                // ignored — best-effort during Load/Dispose
            }
        }
        finally
        {
            bag.Output = null;
        }

        try
        {
            bag.Active?.StopPump();
        }
        finally
        {
            bag.Active?.Dispose();
            bag.Active = null;
        }

        try
        {
            bag.Next?.StopPump();
        }
        finally
        {
            bag.Next?.Dispose();
            bag.Next = null;
        }

        bag.SessionCts?.Dispose();
        bag.SessionCts = null;
    }

    private async Task CompleteTeardownAsync(SessionTeardownBag bag)
    {
        try
        {
            await WaitForOutputShutdownAsync().ConfigureAwait(false);
        }
        catch
        {
            // prior queued shutdown failure must not block releasing this session
        }

        try
        {
            await ShutdownAndDisposeAsync(bag.Output).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                bag.Output?.Dispose();
            }
            catch
            {
                // ignored
            }
        }
        finally
        {
            bag.Output = null;
        }

        try
        {
            if (bag.Active is not null)
            {
                await bag.Active.StopPumpAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            bag.Active?.Dispose();
            bag.Active = null;
        }

        try
        {
            if (bag.Next is not null)
            {
                await bag.Next.StopPumpAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            bag.Next?.Dispose();
            bag.Next = null;
        }

        bag.SessionCts?.Dispose();
        bag.SessionCts = null;
    }

    private sealed class SessionTeardownBag
    {
        public IAudioOutput? Output;
        public TrackDecodePipeline? Active;
        public TrackDecodePipeline? Next;
        public CancellationTokenSource? SessionCts;
    }

    /// <summary>
    /// Stops transport under the command gate. When <paramref name="resetToStart"/> is true and the
    /// decoder can seek, returns the active pipeline so the caller can rewind it <b>off</b> the gate
    /// via <see cref="ApplyDecoderResetToStart"/>.
    /// </summary>
    private TrackDecodePipeline? StopTransport_NoLock(bool resetToStart, bool raiseCompletion)
    {
        Volatile.Write(ref _acceptEndOfStreamCompletion, 0);

        _output?.Stop();
        _active?.StopPump();

        if (_active is not null)
        {
            _active.Ring.Clear();
        }

        _sampleSource?.ResetEndOfStream();
        _effects.ResetProcessingState();
        _effects.DisposeRetired();

        TrackDecodePipeline? decoderReset = null;
        if (resetToStart && _active is not null)
        {
            _timelineOriginSamples = 0;
            Interlocked.Exchange(ref _samplesDecoded, 0);
            Interlocked.Exchange(ref _samplesConsumed, 0);
            RebasePositionClock_NoLock(TimeSpan.Zero);
            if (_active.Decoder.CanSeek)
            {
                decoderReset = _active;
            }
            else
            {
                EnsureActivePumpRunning_NoLock();
            }
        }
        else
        {
            RebasePositionClock_NoLock(EstimateAudiblePosition());
        }

        SetState_NoLock(PlaybackState.Stopped);

        if (raiseCompletion)
        {
            RaiseCompletion();
        }

        return decoderReset;
    }

    /// <summary>
    /// Handles <see cref="IAudioOutput.Stopped"/>. Internal for deterministic stale-sender tests.
    /// </summary>
    internal void OnOutputStopped(object? sender, AudioOutputStoppedEventArgs e)
    {
        // Never invoke ErrorOccurred on the output callback / Stopped thread while also taking the
        // command gate: Post handling onto a worker and raise errors from HandleOutputStopped after
        // classifying EOS vs failure (EOS must not RaiseError).
        // Capture sender so a deferred handler cannot apply a stale Stopped to a replacement output.
        if (!_gate.Wait(0))
        {
            _ = Task.Run(() => HandleOutputStopped(sender, e));
            return;
        }

        try
        {
            HandleOutputStopped_NoLock(sender, e);
        }
        catch (ObjectDisposedException)
        {
            // Gate may be contended during teardown; ignore late callbacks.
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // ignored — gate is never disposed now, but keep defensive
            }
        }
    }

    private void HandleOutputStopped(object? sender, AudioOutputStoppedEventArgs e)
    {
        try
        {
            _gate.Wait();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            HandleOutputStopped_NoLock(sender, e);
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // ignored
            }
        }
    }

    private void HandleOutputStopped_NoLock(object? sender, AudioOutputStoppedEventArgs e)
    {
        if (_disposed || !_hasSource)
        {
            return;
        }

        // Ignore Stopped from an output that is no longer current (stale deferred callback).
        if (!ReferenceEquals(sender, _output))
        {
            return;
        }

        if (e.ReachedEndOfStream)
        {
            if (Volatile.Read(ref _acceptEndOfStreamCompletion) == 0)
            {
                return;
            }

            // If a next track became ready after EOS signaling started, do not complete the session.
            if (_next.State == NextTrackState.Ready)
            {
                return;
            }

            // Ignore spurious device EOS at a gapless A|B boundary: the sample source is still live
            // after next was promoted (slot empty) but before the session has truly ended.
            if (_sampleSource is { IsEndOfStream: false })
            {
                return;
            }

            if (_state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering)
            {
                _activeTrackGeneration++;
                var decoderReset = StopTransport_NoLock(resetToStart: true, raiseCompletion: true);
                if (decoderReset is not null)
                {
                    // Must not Decoder.Seek under the gate (may block on Media Foundation).
                    _ = Task.Run(() => ApplyDecoderResetToStart(decoderReset));
                }
            }

            return;
        }

        if (e.Exception is null)
        {
            return;
        }

        RaiseError(e.Exception);

        // Device loss / output failure during playback or pause.
        if (_state is PlaybackState.Playing or PlaybackState.Paused)
        {
            if (_state == PlaybackState.Playing)
            {
                _heldPosition = EstimateAudiblePosition();
                _outputRecoveryIntent = OutputRecoveryIntent.ResumePlaying;
            }
            else
            {
                _outputRecoveryIntent = OutputRecoveryIntent.RemainPaused;
            }

            Volatile.Write(ref _acceptEndOfStreamCompletion, 0);
            var lostOutput = DetachOutput_NoLock();
            var failureKind = OutputFailureHelpers.KindOf(e.Exception);
            // Exclusive init/format/busy failures are not "device lost": auto-recovering with the
            // same Exclusive configuration recreates the failure in a tight loop. Leave the player
            // Unavailable with recovery intent so the app can reconfigure or retry deliberately.
            var autoRecover = failureKind is AudioOutputFailureKind.DeviceUnavailable
                or AudioOutputFailureKind.Unspecified
                or AudioOutputFailureKind.ShutdownFailed;

            _outputDeviceStatus = autoRecover
                ? OutputDeviceStatus.Switching
                : OutputDeviceStatus.Unavailable;
            if (!autoRecover
                && failureKind != AudioOutputFailureKind.Unspecified
                && failureKind != AudioOutputFailureKind.ShutdownFailed)
            {
                _outputFallbackReason = OutputFailureHelpers.ToFallbackReason(failureKind);
                _effectiveOutputMode = null;
            }

            SetState_NoLock(PlaybackState.Stopped);
            // Shutdown off the gate path so Stop/Load are not blocked on WASAPI Join.
            // Serialize with WaitForOutputShutdownAsync before opening a replacement client.
            QueueOutputShutdown(lostOutput);

            if (autoRecover)
            {
                ScheduleDeviceRecovery(OutputDeviceChangeReason.DeviceLostFallback);
            }
            else
            {
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
            }
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(() => ScheduleDeviceRecovery(OutputDeviceChangeReason.Recovered));
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultOutputDeviceChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            if (_disposed)
            {
                return;
            }

            // Explicit selections ignore unrelated default changes for binding.
            if (PreferredOutputDeviceId is not null)
            {
                return;
            }

            if (string.Equals(ActiveOutputDeviceId, e.NewDeviceId, StringComparison.Ordinal)
                && _outputDeviceStatus == OutputDeviceStatus.Available
                && _output is not null)
            {
                return;
            }

            try
            {
                await SetOutputDeviceAsync(null, CancellationToken.None).ConfigureAwait(false);
                // Override reason for apps watching default changes.
                // SetOutputDeviceAsync already raised SelectionChanged; raise an additional
                // SystemDefaultChanged only when following default and id actually changed.
            }
            catch (Exception ex) when (ex is AudioException or InvalidOperationException)
            {
                RaiseError(ex);
            }
        });
    }

    private void ScheduleDeviceRecovery(OutputDeviceChangeReason reason)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _recoveryInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RecoverOutputAsync(reason).ConfigureAwait(false);
            }
            catch (Exception) when (!_disposed)
            {
                // Attach/Play failures inside recovery already raise ErrorOccurred; swallow so the
                // fire-and-forget task does not become an unobserved exception.
            }
            catch (ObjectDisposedException)
            {
                // ignored
            }
            finally
            {
                Interlocked.Exchange(ref _recoveryInFlight, 0);
            }
        });
    }

    private async Task RecoverOutputAsync(OutputDeviceChangeReason reason)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        int switchGeneration = 0;
        CancellationToken switchToken = CancellationToken.None;
        TimeSpan heldPosition = TimeSpan.Zero;
        string? effectiveId = null;
        string? preferred = null;
        var intent = OutputRecoveryIntent.None;
        var needsCreate = false;
        var releasedExclusive = false;
        IAudioOutput? oldOutput = null;
        try
        {
            if (_disposed || !_hasSource)
            {
                return;
            }

            // A user-initiated rebind owns Switching. Device-loss recovery uses DeviceLostFallback
            // and must proceed; DevicesChanged (Recovered) must not cancel the user switch.
            if (_outputDeviceStatus == OutputDeviceStatus.Switching
                && reason == OutputDeviceChangeReason.Recovered)
            {
                return;
            }

            preferred = _preferredOutputDeviceId;
            intent = _outputRecoveryIntent;
            heldPosition = _heldPosition;

            // Preferred returned while on fallback: notify only; do not auto-restore.
            if (preferred is not null
                && _outputDeviceStatus == OutputDeviceStatus.PreferredUnavailable
                && _outputDevices.TryGetDevice(preferred, out var preferredDevice)
                && preferredDevice?.State == AudioDeviceState.Active
                && reason == OutputDeviceChangeReason.Recovered)
            {
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Recovered);
                return;
            }

            if (preferred is not null
                && _outputDevices.TryGetDevice(preferred, out var stillPreferred)
                && stillPreferred?.State == AudioDeviceState.Active)
            {
                effectiveId = preferred;
            }
            else
            {
                var def = _outputDevices.GetDefaultDevice();
                effectiveId = def?.State == AudioDeviceState.Active ? def.Id : null;
            }

            if (effectiveId is null)
            {
                // Detach and shut down below — do not return before ShutdownAndDisposeAsync.
                oldOutput = DetachOutput_NoLock();
                _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                SetState_NoLock(PlaybackState.Stopped);
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
                needsCreate = false;
            }
            else if (_output is not null
                && string.Equals(_activeOutputDeviceId, effectiveId, StringComparison.Ordinal)
                && _outputDeviceStatus is OutputDeviceStatus.Available or OutputDeviceStatus.PreferredUnavailable)
            {
                return;
            }
            else
            {
                BeginOutputSwitch_NoLock(CancellationToken.None, out switchGeneration, out switchToken);
                releasedExclusive = _effectiveOutputMode == AudioOutputMode.Exclusive;
                oldOutput = DetachOutput_NoLock();
                _outputDeviceStatus = OutputDeviceStatus.Switching;
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Switching);
                needsCreate = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        await WaitForOutputShutdownAsync().ConfigureAwait(false);

        try
        {
            await ShutdownAndDisposeAsync(oldOutput).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!_disposed && (needsCreate ? switchGeneration == _outputSwitchGeneration : true))
                {
                    _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                    RaiseError(ex);
                    RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
                }
            }
            finally
            {
                _gate.Release();
            }

            return;
        }

        if (!needsCreate)
        {
            return;
        }

        if (switchGeneration != Volatile.Read(ref _outputSwitchGeneration))
        {
            return;
        }

        if (releasedExclusive || _outputConfiguration.Mode == AudioOutputMode.Exclusive)
        {
            await Task.Delay(ExclusiveEndpointReleaseGrace, switchToken).ConfigureAwait(false);
            if (switchGeneration != Volatile.Read(ref _outputSwitchGeneration))
            {
                return;
            }
        }

        if (effectiveId is null)
        {
            return;
        }

        IAudioOutput? created = null;
        try
        {
            created = await _outputFactory
                .CreateOutputAsync(BuildCreateRequest(effectiveId, _outputConfiguration.Mode), switchToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_disposed || switchGeneration != _outputSwitchGeneration)
                {
                    return;
                }

                _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                _effectiveOutputMode = null;
                RaiseError(ex is AudioException ? ex : new AudioOutputException("Output recovery failed.", ex));
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
            }
            finally
            {
                _gate.Release();
            }

            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed || switchGeneration != _outputSwitchGeneration || !_hasSource || _sampleSource is null)
            {
                created.Dispose();
                return;
            }

            // Prefer position updates made during the recovery window.
            heldPosition = _heldPosition;
            intent = _outputRecoveryIntent;

            try
            {
                AttachOutput_NoLock(created, heldPosition, _outputConfiguration.Mode);
                created = null;
            }
            catch (Exception ex)
            {
                created?.Dispose();
                created = null;
                var wrapped = ex is AudioException
                    ? ex
                    : new AudioOutputException("Output recovery failed to initialize.", ex);
                ApplyStrictOutputFailure_NoLock(
                    wasPlaying: intent == OutputRecoveryIntent.ResumePlaying,
                    wasPaused: intent == OutputRecoveryIntent.RemainPaused,
                    heldPosition,
                    wrapped);
                RaiseError(wrapped);
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
                return;
            }

            if (preferred is not null
                && !string.Equals(_activeOutputDeviceId, preferred, StringComparison.Ordinal))
            {
                _outputDeviceStatus = OutputDeviceStatus.PreferredUnavailable;
                reason = OutputDeviceChangeReason.DeviceLostFallback;
            }
            else
            {
                _outputDeviceStatus = OutputDeviceStatus.Available;
            }

            try
            {
                if (intent == OutputRecoveryIntent.ResumePlaying)
                {
                    EnsureActivePumpRunning_NoLock();
                    _active?.Prime(_options.PrimeDuration);
                    _completionRaised = false;
                    Volatile.Write(ref _acceptEndOfStreamCompletion, 1);
                    SetState_NoLock(PlaybackState.Playing);
                    _output!.Play();
                    _outputRecoveryIntent = OutputRecoveryIntent.None;
                }
                else if (intent == OutputRecoveryIntent.RemainPaused)
                {
                    SetState_NoLock(PlaybackState.Paused);
                    _outputRecoveryIntent = OutputRecoveryIntent.None;
                }

                RaiseOutputDeviceChanged(reason == OutputDeviceChangeReason.DeviceLostFallback
                    ? OutputDeviceChangeReason.DeviceLostFallback
                    : OutputDeviceChangeReason.Recovered);
            }
            catch (Exception ex)
            {
                var wrapped = ex is AudioException
                    ? ex
                    : new AudioOutputException("Output recovery failed to start playback.", ex);
                var lost = DetachOutput_NoLock();
                ApplyStrictOutputFailure_NoLock(
                    wasPlaying: intent == OutputRecoveryIntent.ResumePlaying,
                    wasPaused: intent == OutputRecoveryIntent.RemainPaused,
                    heldPosition,
                    wrapped);
                RaiseError(wrapped);
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
                QueueOutputShutdown(lost);
            }
        }
        finally
        {
            created?.Dispose();
            _gate.Release();
        }
    }

    private void CancelOutputSwitch_NoLock()
    {
        try
        {
            _outputSwitchCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        _outputSwitchCts?.Dispose();
        _outputSwitchCts = null;
        _outputSwitchGeneration++;
    }

    private void BeginOutputSwitch_NoLock(
        CancellationToken cancellationToken,
        out int switchGeneration,
        out CancellationToken switchToken)
    {
        CancelOutputSwitch_NoLock();
        switchGeneration = _outputSwitchGeneration;
        _outputSwitchCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        switchToken = _outputSwitchCts.Token;
    }

    private void HandleUnderrunFromCallback()
    {
        if (Interlocked.CompareExchange(ref _underrunLatched, 1, 0) != 0)
        {
            return;
        }

        // Freeze to sample-based estimate — device clocks advance on silence and would drift.
        var active = _active;
        if (active is not null && Volatile.Read(ref _positionFrozen) == 0)
        {
            var consumed = _timelineOriginSamples + Interlocked.Read(ref _samplesConsumed);
            _frozenAudiblePosition = ClampToDuration(
                active.Format.SamplesToTime(Math.Max(0, consumed)));
            Volatile.Write(ref _positionFrozen, 1);
        }

        Interlocked.Increment(ref _underrunCount);
        try
        {
            Underrun?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // consumer exceptions must not escape the audio path
        }
    }

    private void OnSamplesConsumedFromCallback(int n)
    {
        Interlocked.Add(ref _samplesConsumed, n);
        if (Interlocked.Exchange(ref _positionFrozen, 0) == 1)
        {
            // Rebase device-clock path to the frozen audible position; samplesConsumed did not
            // advance during silence, so the sample-based path stays consistent after unfreeze.
            _logicalPositionAtAnchor = _frozenAudiblePosition;
            _deviceClockAnchor = _output?.TryGetRenderedPosition() ?? TimeSpan.Zero;
        }

        Interlocked.Exchange(ref _underrunLatched, 0);
    }

    private TimeSpan EstimateAudiblePosition()
    {
        if (_active is null)
        {
            return TimeSpan.Zero;
        }

        if (Volatile.Read(ref _positionFrozen) == 1)
        {
            return _frozenAudiblePosition;
        }

        // Network stalls: device clocks keep advancing on silence — use samplesConsumed instead.
        var netStatus = _networkStatus;
        if (_state == PlaybackState.Playing
            && netStatus is NetworkTransportStatus.Buffering or NetworkTransportStatus.Reconnecting)
        {
            var stallConsumed = _timelineOriginSamples + Interlocked.Read(ref _samplesConsumed);
            return ClampToDuration(_active.Format.SamplesToTime(Math.Max(0, stallConsumed)));
        }

        var rendered = _output?.TryGetRenderedPosition();
        if (rendered is not null)
        {
            var delta = rendered.Value - _deviceClockAnchor;
            if (delta < TimeSpan.Zero)
            {
                delta = TimeSpan.Zero;
            }

            return ClampToDuration(_logicalPositionAtAnchor + delta);
        }

        var consumed = _timelineOriginSamples + Interlocked.Read(ref _samplesConsumed);
        var buffered = _active.Ring.Count;
        var latencySamples = 0L;
        if (_output is not null)
        {
            latencySamples = _active.Format.TimeToSamples(_output.CurrentLatency);
        }

        var audibleSamples = Math.Max(0, consumed - buffered - latencySamples);
        return ClampToDuration(_active.Format.SamplesToTime(audibleSamples));
    }

    private void RebasePositionClock_NoLock(TimeSpan logicalPosition)
    {
        _logicalPositionAtAnchor = logicalPosition;
        _deviceClockAnchor = _output?.TryGetRenderedPosition() ?? TimeSpan.Zero;
        _heldPosition = logicalPosition;
        Interlocked.Increment(ref _positionIntentGeneration);
    }

    private TimeSpan ClampToDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (_hasKnownDuration && _duration > TimeSpan.Zero && value > _duration)
        {
            return _duration;
        }

        return value;
    }

    private TransitionSampleSource CreateTransitionSampleSource_NoLock(AudioFormat format) =>
        new(
            format,
            _effects,
            getVolume: () => Volatile.Read(ref _volume),
            onSamplesConsumed: OnSamplesConsumedFromCallback,
            onUnderrun: HandleUnderrunFromCallback,
            isPlaying: () => _state == PlaybackState.Playing,
            getActive: () =>
            {
                lock (_pipelineSync)
                {
                    return _active;
                }
            },
            tryPromoteNext: TryPromoteNextFromCallback,
            hasPendingNext: () =>
            {
                var s = _next.State;
                return s is NextTrackState.Ready or NextTrackState.Loading;
            },
            scheduleTransitioned: ScheduleTrackTransitioned);

    private void BindNetworkInfo_NoLock(INetworkTransportInfo? info)
    {
        UnbindNetworkInfo_NoLock(setIdle: info is null);
        if (info is null)
        {
            return;
        }

        _networkInfo = info;
        info.TransportChanged += OnNetworkTransportChanged;
        MirrorNetworkStatusFromInfo(info);
    }

    private void UnbindNetworkInfo_NoLock(bool setIdle)
    {
        var info = _networkInfo;
        if (info is not null)
        {
            info.TransportChanged -= OnNetworkTransportChanged;
            _networkInfo = null;
        }

        if (setIdle)
        {
            _networkStatus = NetworkTransportStatus.Idle;
            Volatile.Write(ref _networkBytesBuffered, 0);
        }
    }

    private void MirrorNetworkStatusFromInfo(INetworkTransportInfo info)
    {
        _networkStatus = info.TransportStatus;
        Volatile.Write(ref _networkBytesBuffered, info.NetworkBytesBuffered);
    }

    private void OnNetworkTransportChanged(object? sender, EventArgs e)
    {
        var info = _networkInfo;
        if (info is null || (sender is not null && !ReferenceEquals(sender, info)))
        {
            return;
        }

        MirrorNetworkStatusFromInfo(info);
        try
        {
            NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // ignored
        }
    }

    private void SetState_NoLock(PlaybackState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        var handler = StateChanged;
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // ignored
        }
    }

    private void RaiseCompletion()
    {
        if (_completionRaised)
        {
            return;
        }

        _completionRaised = true;
        try
        {
            PlaybackCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // ignored
        }
    }

    private void RaiseError(Exception exception)
    {
        try
        {
            ErrorOccurred?.Invoke(this, exception);
        }
        catch
        {
            // ignored
        }
    }

    private void RaiseOutputDeviceChanged(OutputDeviceChangeReason reason)
    {
        var args = new OutputDeviceChangedEventArgs(
            PreferredOutputDeviceId,
            ActiveOutputDeviceId,
            _outputDeviceStatus,
            _outputRecoveryIntent,
            reason);

        // Never invoke under assumptions that callers may re-enter; already off audio thread.
        try
        {
            OutputDeviceChanged?.Invoke(this, args);
        }
        catch
        {
            // ignored
        }
    }

    private void RaiseOutputConfigurationChanged()
    {
        var args = new OutputConfigurationChangedEventArgs(
            _outputConfiguration,
            _effectiveOutputMode,
            _outputFallbackReason,
            _output?.CurrentLatency);

        try
        {
            OutputConfigurationChanged?.Invoke(this, args);
        }
        catch
        {
            // ignored
        }
    }

    private AudioOutputCreateRequest BuildCreateRequest(string? effectiveDeviceId, AudioOutputMode mode) =>
        new(
            effectiveDeviceId,
            mode,
            _outputConfiguration.RequestedLatency,
            _hasSource ? _sessionFormat : null);

    private async Task FinishOutputRebindAsync(
        string? preferredDeviceId,
        string? effectiveDeviceId,
        int switchGeneration,
        CancellationToken switchToken,
        bool wasPlaying,
        bool wasPaused,
        TimeSpan heldPosition,
        bool raiseConfigurationChanged)
    {
        var config = _outputConfiguration;
        var requestedMode = config.Mode;
        var sessionFormat = _sessionFormat;
        var useExclusive = requestedMode == AudioOutputMode.Exclusive;
        ExclusiveFallbackReason pendingFallbackReason = ExclusiveFallbackReason.None;

        if (useExclusive)
        {
            var probeRequest = BuildCreateRequest(effectiveDeviceId, AudioOutputMode.Exclusive);
            AudioOutputFormatProbeResult probe;
            try
            {
                probe = await _outputFactory
                    .ProbeOutputFormatAsync(probeRequest, sessionFormat, switchToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ThrowIfSwitchSuperseded(switchGeneration, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                var wrapped = ex is AudioException
                    ? ex
                    : new AudioOutputException(
                        "Exclusive format probe failed.",
                        AudioOutputFailureKind.InitializationFailed,
                        ex);
                if (config.ExclusiveFallback == ExclusiveModeFallback.Shared
                    && OutputFailureHelpers.IsFallbackEligible(OutputFailureHelpers.KindOf(wrapped)))
                {
                    useExclusive = false;
                    pendingFallbackReason = OutputFailureHelpers.ToFallbackReason(
                        OutputFailureHelpers.KindOf(wrapped));
                }
                else
                {
                    await FailSwitchAfterTeardownAsync(
                        switchGeneration,
                        wasPlaying,
                        wasPaused,
                        heldPosition,
                        wrapped).ConfigureAwait(false);
                    if (raiseConfigurationChanged)
                    {
                        RaiseOutputConfigurationChanged();
                    }

                    throw;
                }

                probe = default;
            }

            if (useExclusive)
            {
                if (!probe.Supported)
                {
                    if (config.ExclusiveFallback == ExclusiveModeFallback.Shared
                        && OutputFailureHelpers.IsFallbackEligible(probe.FailureKind))
                    {
                        useExclusive = false;
                        pendingFallbackReason = OutputFailureHelpers.ToFallbackReason(probe.FailureKind);
                    }
                    else
                    {
                        var fail = new AudioOutputException(
                            "Exclusive output is not available for the preferred format.",
                            probe.FailureKind == AudioOutputFailureKind.Unspecified
                                ? AudioOutputFailureKind.UnsupportedFormat
                                : probe.FailureKind);
                        await FailSwitchAfterTeardownAsync(
                            switchGeneration,
                            wasPlaying,
                            wasPaused,
                            heldPosition,
                            fail).ConfigureAwait(false);
                        if (raiseConfigurationChanged)
                        {
                            RaiseOutputConfigurationChanged();
                        }

                        throw fail;
                    }
                }
                else if (!SessionFormatEquals(sessionFormat, probe.RequiredSessionFormat))
                {
                    await RebuildSessionForOutputFormatAsync(
                        probe.RequiredSessionFormat,
                        heldPosition,
                        switchGeneration,
                        switchToken).ConfigureAwait(false);
                    sessionFormat = probe.RequiredSessionFormat;
                }
            }
        }

        ThrowIfSwitchSuperseded(switchGeneration, switchToken);

        var modeToCreate = useExclusive ? AudioOutputMode.Exclusive : AudioOutputMode.Shared;
        IAudioOutput? created = null;
        try
        {
            created = await _outputFactory
                .CreateOutputAsync(BuildCreateRequest(effectiveDeviceId, modeToCreate), switchToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThrowIfSwitchSuperseded(switchGeneration, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var wrapped = ex is AudioException
                ? ex
                : new AudioOutputException("Failed to open the output device.", ex);

            if (useExclusive
                && config.ExclusiveFallback == ExclusiveModeFallback.Shared
                && OutputFailureHelpers.IsFallbackEligible(OutputFailureHelpers.KindOf(wrapped)))
            {
                pendingFallbackReason = OutputFailureHelpers.ToFallbackReason(
                    OutputFailureHelpers.KindOf(wrapped));
                useExclusive = false;
                modeToCreate = AudioOutputMode.Shared;
                created = await _outputFactory
                    .CreateOutputAsync(BuildCreateRequest(effectiveDeviceId, AudioOutputMode.Shared), switchToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await FailSwitchAfterTeardownAsync(
                    switchGeneration,
                    wasPlaying,
                    wasPaused,
                    heldPosition,
                    wrapped).ConfigureAwait(false);
                if (raiseConfigurationChanged)
                {
                    RaiseOutputConfigurationChanged();
                }

                throw;
            }
        }

        // Exclusive Initialize/Play immediately after Create still AVs GetBuffer on some
        // endpoints (especially after a prior Shared device switch). Brief settle helps.
        if (modeToCreate == AudioOutputMode.Exclusive)
        {
            try
            {
                await Task.Delay(ExclusiveEndpointInitGrace, switchToken).ConfigureAwait(false);
                ThrowIfSwitchSuperseded(switchGeneration, switchToken);
            }
            catch
            {
                ShutdownAndDispose(created);
                created = null;
                throw;
            }
        }

        var attachPosition = heldPosition;
        Exception? attachError = null;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                created.Dispose();
                created = null;
                throw new ObjectDisposedException(nameof(AudioPlayer));
            }

            if (switchGeneration != _outputSwitchGeneration)
            {
                created.Dispose();
                created = null;
                throw CreateSupersededException();
            }

            if (!_hasSource || _sampleSource is null)
            {
                created.Dispose();
                created = null;
                _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
                throw CreateSupersededException();
            }

            // Prefer the latest rebased intent (including seek-to-zero during the switch window).
            attachPosition = _heldPosition;

            try
            {
                AttachOutput_NoLock(created, attachPosition, modeToCreate);
                created = null;
            }
            catch (Exception ex)
            {
                ShutdownAndDispose(created);
                created = null;
                attachError = ex is AudioException
                    ? ex
                    : new AudioOutputException("Failed to initialize the output device.", ex);
            }
        }
        finally
        {
            created?.Dispose();
            created = null;
            _gate.Release();
        }

        var deferExclusivePlay = false;
        if (attachError is not null)
        {
            if (modeToCreate == AudioOutputMode.Exclusive
                && config.ExclusiveFallback == ExclusiveModeFallback.Shared
                && OutputFailureHelpers.IsFallbackEligible(OutputFailureHelpers.KindOf(attachError)))
            {
                pendingFallbackReason = OutputFailureHelpers.ToFallbackReason(
                    OutputFailureHelpers.KindOf(attachError));
                ThrowIfSwitchSuperseded(switchGeneration, switchToken);
                try
                {
                    created = await _outputFactory
                        .CreateOutputAsync(
                            BuildCreateRequest(effectiveDeviceId, AudioOutputMode.Shared),
                            switchToken)
                        .ConfigureAwait(false);
                }
                catch (Exception fallbackEx)
                {
                    await FailSwitchAfterTeardownAsync(
                        switchGeneration,
                        wasPlaying,
                        wasPaused,
                        heldPosition,
                        fallbackEx is AudioException
                            ? fallbackEx
                            : new AudioOutputException("Shared-mode fallback failed.", fallbackEx))
                        .ConfigureAwait(false);
                    if (raiseConfigurationChanged)
                    {
                        RaiseOutputConfigurationChanged();
                    }

                    throw;
                }

                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (_disposed || switchGeneration != _outputSwitchGeneration || !_hasSource || _sampleSource is null)
                    {
                        created.Dispose();
                        created = null;
                        throw CreateSupersededException();
                    }

                    try
                    {
                        AttachOutput_NoLock(created, attachPosition, AudioOutputMode.Shared);
                        created = null;
                        modeToCreate = AudioOutputMode.Shared;
                        attachError = null;
                    }
                    catch (Exception fallbackAttachEx)
                    {
                        ShutdownAndDispose(created);
                        created = null;
                        attachError = fallbackAttachEx is AudioException
                            ? fallbackAttachEx
                            : new AudioOutputException(
                                "Shared-mode fallback initialize failed.",
                                fallbackAttachEx);
                    }

                    if (attachError is null)
                    {
                        deferExclusivePlay = CompleteRebindTransport_NoLock(
                            preferredDeviceId,
                            wasPlaying,
                            wasPaused,
                            requestedMode,
                            modeToCreate,
                            pendingFallbackReason);
                    }
                    else
                    {
                        ApplyStrictOutputFailure_NoLock(wasPlaying, wasPaused, heldPosition, attachError);
                    }
                }
                finally
                {
                    created?.Dispose();
                    _gate.Release();
                }

                if (attachError is not null)
                {
                    if (raiseConfigurationChanged)
                    {
                        RaiseOutputConfigurationChanged();
                    }

                    throw attachError;
                }
            }
            else
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (switchGeneration == _outputSwitchGeneration && !_disposed)
                    {
                        ApplyStrictOutputFailure_NoLock(wasPlaying, wasPaused, heldPosition, attachError);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                if (raiseConfigurationChanged
                    || requestedMode == AudioOutputMode.Exclusive)
                {
                    RaiseOutputConfigurationChanged();
                }

                throw attachError;
            }
        }
        else
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (switchGeneration != _outputSwitchGeneration || _disposed || _output is null)
                {
                    throw CreateSupersededException();
                }

                deferExclusivePlay = CompleteRebindTransport_NoLock(
                    preferredDeviceId,
                    wasPlaying,
                    wasPaused,
                    requestedMode,
                    modeToCreate,
                    pendingFallbackReason);
            }
            finally
            {
                _gate.Release();
            }
        }

        if (deferExclusivePlay)
        {
            await StartDeferredExclusivePlaybackAsync(switchGeneration, switchToken).ConfigureAwait(false);
        }

        if (raiseConfigurationChanged
            || requestedMode == AudioOutputMode.Exclusive
            || pendingFallbackReason != ExclusiveFallbackReason.None)
        {
            RaiseOutputConfigurationChanged();
        }
    }

    /// <returns>
    /// <see langword="true"/> when exclusive playback was armed but <see cref="IAudioOutput.Play"/>
    /// was deferred so the caller can settle the endpoint before starting the WASAPI play thread.
    /// </returns>
    private bool CompleteRebindTransport_NoLock(
        string? preferredDeviceId,
        bool wasPlaying,
        bool wasPaused,
        AudioOutputMode requestedMode,
        AudioOutputMode modeToCreate,
        ExclusiveFallbackReason pendingFallbackReason)
    {
        _outputFallbackReason = modeToCreate == AudioOutputMode.Shared
            && requestedMode == AudioOutputMode.Exclusive
                ? pendingFallbackReason
                : ExclusiveFallbackReason.None;

        _outputDeviceStatus = preferredDeviceId is not null
            && !string.Equals(_activeOutputDeviceId, preferredDeviceId, StringComparison.Ordinal)
                ? OutputDeviceStatus.PreferredUnavailable
                : OutputDeviceStatus.Available;

        var deferExclusivePlay = false;
        // Prefer Pause that occurred during the detached switch window over the start-of-switch snapshot.
        var honorPause = _state == PlaybackState.Paused
            || wasPaused
            || _outputRecoveryIntent == OutputRecoveryIntent.RemainPaused;
        var honorPlay = !honorPause
            && (wasPlaying || _outputRecoveryIntent == OutputRecoveryIntent.ResumePlaying);

        if (honorPlay)
        {
            EnsureActivePumpRunning_NoLock();
            _active?.Prime(_options.PrimeDuration);
            _completionRaised = false;
            Volatile.Write(ref _acceptEndOfStreamCompletion, 1);
            SetState_NoLock(PlaybackState.Playing);
            if (modeToCreate == AudioOutputMode.Exclusive)
            {
                // Defer Play: starting the exclusive play thread in the same breath as Init
                // still AVs GetBuffer on some drivers after a Shared device switch.
                deferExclusivePlay = true;
            }
            else
            {
                _output!.Play();
            }

            _outputRecoveryIntent = OutputRecoveryIntent.None;
        }
        else if (honorPause)
        {
            SetState_NoLock(PlaybackState.Paused);
            _outputRecoveryIntent = OutputRecoveryIntent.None;
        }
        else
        {
            SetState_NoLock(PlaybackState.Stopped);
        }

        RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
        return deferExclusivePlay;
    }

    private async Task StartDeferredExclusivePlaybackAsync(
        int switchGeneration,
        CancellationToken switchToken)
    {
        await Task.Delay(ExclusiveEndpointInitGrace, switchToken).ConfigureAwait(false);
        ThrowIfSwitchSuperseded(switchGeneration, switchToken);

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfSwitchSuperseded(switchGeneration, switchToken);
            if (_output is null || _state != PlaybackState.Playing)
            {
                return;
            }

            _output.Play();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyStrictOutputFailure_NoLock(
        bool wasPlaying,
        bool wasPaused,
        TimeSpan heldPosition,
        Exception error)
    {
        _activeOutputDeviceId = null;
        _effectiveOutputMode = null;
        _outputDeviceStatus = OutputDeviceStatus.Unavailable;
        _heldPosition = heldPosition;
        if (wasPlaying)
        {
            _outputRecoveryIntent = OutputRecoveryIntent.ResumePlaying;
        }
        else if (wasPaused)
        {
            _outputRecoveryIntent = OutputRecoveryIntent.RemainPaused;
        }

        var kind = OutputFailureHelpers.KindOf(error);
        if (kind != AudioOutputFailureKind.Unspecified && kind != AudioOutputFailureKind.ShutdownFailed)
        {
            _outputFallbackReason = OutputFailureHelpers.ToFallbackReason(kind);
        }

        SetState_NoLock(PlaybackState.Stopped);
        RaiseError(error);
        RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
    }

    private static bool SessionFormatEquals(AudioFormat a, AudioFormat b) =>
        a.SampleRate == b.SampleRate
        && a.ChannelCount == b.ChannelCount
        && a.SampleFormat == b.SampleFormat;

    private async Task RebuildSessionForOutputFormatAsync(
        AudioFormat requiredFormat,
        TimeSpan heldPosition,
        int switchGeneration,
        CancellationToken switchToken)
    {
        var hasNext = _next.State is NextTrackState.Ready or NextTrackState.Loading or NextTrackState.Failed
            && _next.Source is not null;

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        AudioSource activeSource;
        try
        {
            ThrowIfSwitchSuperseded(switchGeneration, switchToken);
            if (!_hasSource || _active is null)
            {
                throw CreateSupersededException();
            }

            activeSource = _currentSource
                ?? _active.Source
                ?? FileAudioSource.Create(_sourcePath ?? _active.Path);
            _heldPosition = heldPosition;

            // Tear down active pipeline but keep session flags for rebuild.
            UnbindNetworkInfo_NoLock(setIdle: true);
            _active.StopPump();
            _sampleSource = null;
            var oldActive = _active;
            _active = null;
            oldActive.Dispose();

            if (hasNext)
            {
                // Source retained; pipeline disposed — explicit Failed until rebuilt.
                _next.InvalidateKeepPath(
                    new AudioOutputException(
                        "Next track must be rebuilt for the new session output format.",
                        AudioOutputFailureKind.UnsupportedFormat));
            }

            _sessionFormat = requiredFormat;
        }
        finally
        {
            _gate.Release();
        }

        // Rebuild active off-gate.
        IAudioDecoder? opened = null;
        TrackDecodePipeline? pipeline = null;
        try
        {
            ThrowIfSwitchSuperseded(switchGeneration, switchToken);
            opened = await _decoderFactory.CreateAsync(activeSource, switchToken).ConfigureAwait(false);
            var networkInfo = opened as INetworkTransportInfo;
            var converted = _formatConverter.Convert(opened, requiredFormat);
            opened = null;
            pipeline = TrackDecodePipeline.Create(activeSource, converted, _options, networkInfo);

            if (activeSource is HttpAudioSource
                && !pipeline.Decoder.CanSeek
                && heldPosition > TimeSpan.Zero)
            {
                throw new AudioOutputException(
                    "Cannot rebuild a non-seekable HTTP stream at a non-zero position; seamless continuity is impossible.",
                    AudioOutputFailureKind.UnsupportedFormat);
            }

            var rebuildOrigin = heldPosition;
            if (heldPosition > TimeSpan.Zero && pipeline.Decoder.CanSeek)
            {
                var seekResult = pipeline.Decoder.Seek(heldPosition);
                rebuildOrigin = seekResult.ActualPosition ?? heldPosition;
            }

            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ThrowIfSwitchSuperseded(switchGeneration, switchToken);
                _sessionFormat = requiredFormat;
                _currentSource = activeSource;
                Volatile.Write(ref _sourcePath, activeSource.Identifier);
                _duration = pipeline.Duration;
                _hasKnownDuration = pipeline.Decoder.HasKnownDuration;
                _timelineOriginSamples = requiredFormat.TimeToSamples(rebuildOrigin);
                Interlocked.Exchange(ref _samplesDecoded, 0);
                Interlocked.Exchange(ref _samplesConsumed, 0);
                _completionRaised = false;
                Interlocked.Exchange(ref _underrunLatched, 0);
                Volatile.Write(ref _positionFrozen, 0);
                _activeTrackGeneration++;
                _suppressTransitionIdsAtOrBelow = _transitionId;

                BindNetworkInfo_NoLock(networkInfo);

                _active = pipeline;
                pipeline = null;

                _effects.Configure(requiredFormat);
                _effects.ResetProcessingState();
                _effects.DisposeRetired();

                _sampleSource = CreateTransitionSampleSource_NoLock(requiredFormat);

                _heldPosition = rebuildOrigin;
                RebasePositionClock_NoLock(rebuildOrigin);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            pipeline?.Dispose();
            opened?.Dispose();
            throw;
        }

        // Rebuild next track when source is known.
        if (hasNext && _next.TryBeginRebuild(out var nextGeneration, out var rebuildSource))
        {
            IAudioDecoder? nextOpened = null;
            TrackDecodePipeline? nextPipeline = null;
            try
            {
                nextOpened = await _decoderFactory.CreateAsync(rebuildSource, switchToken).ConfigureAwait(false);
                var nextNetwork = nextOpened as INetworkTransportInfo;
                var nextConverted = _formatConverter.Convert(nextOpened, requiredFormat);
                nextOpened = null;
                nextPipeline = TrackDecodePipeline.Create(rebuildSource, nextConverted, _options, nextNetwork);
                nextPipeline.StartPump(
                    _options.Processors,
                    onSamplesWritten: _ => { },
                    onError: ex =>
                    {
                        if (_next.TryCompleteFailed(nextGeneration, ex))
                        {
                            RaiseError(ex);
                        }
                    });
                nextPipeline.Prime(_options.PrimeDuration);
                if (!_next.TryCompleteReady(nextGeneration, nextPipeline))
                {
                    return;
                }

                nextPipeline = null;
            }
            catch (Exception ex)
            {
                nextPipeline?.Dispose();
                nextOpened?.Dispose();
                _next.TryCompleteFailed(nextGeneration, ex);
            }
        }
    }
}
