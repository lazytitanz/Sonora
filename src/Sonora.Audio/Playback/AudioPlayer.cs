using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;
using Sonora.Audio.Playback;
using Sonora.Audio.Seeking;

namespace Sonora.Audio;

/// <summary>
/// Loads audio sources and controls playback. Commands are serialized; decoding runs on a background pump.
/// Events are raised on worker threads and are not marshaled to a UI synchronization context.
/// Re-entering playback commands from event handlers is unsupported and may deadlock.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly IAudioDecoderFactory _decoderFactory;
    private readonly IAudioOutputFactory _outputFactory;
    private readonly IAudioOutputDevices _outputDevices;
    private readonly AudioEngineOptions _options;
    private readonly IAudioFormatConverter _formatConverter;
    private readonly AudioEffectRack _effects;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
    /// <summary>
    /// When 0, end-of-stream notifications from the output device are ignored.
    /// Cleared before explicit <see cref="Stop"/> / teardown so NAudio/WASAPI Stopped
    /// callbacks cannot raise <see cref="PlaybackCompleted"/> after a user stop.
    /// </summary>
    private int _acceptEndOfStreamCompletion;

    private TrackDecodePipeline? _active;
    private IAudioOutput? _output;
    private TransitionSampleSource? _sampleSource;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _outputSwitchCts;
    private AudioFormat _sessionFormat;
    private string? _sourcePath;

    private string? _preferredOutputDeviceId;
    private string? _activeOutputDeviceId;
    private volatile OutputDeviceStatus _outputDeviceStatus = OutputDeviceStatus.Available;
    private OutputRecoveryIntent _outputRecoveryIntent = OutputRecoveryIntent.None;
    private int _recoveryInFlight;

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
    {
        _decoderFactory = decoderFactory ?? throw new ArgumentNullException(nameof(decoderFactory));
        _outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));
        _options = options ?? new AudioEngineOptions();
        _formatConverter = _options.FormatConverter ?? new PassthroughFormatConverter();
        _effects = new AudioEffectRack(_options.EffectFactory);
        _outputDevices = outputDevices ?? NullAudioOutputDevices.Instance;
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

    /// <summary>Path of the logically active track (updates at the output-consumption handoff).</summary>
    public string? SourcePath => Volatile.Read(ref _sourcePath);

    /// <summary>Path of the pending next track when loading or ready; otherwise <see langword="null"/>.</summary>
    public string? NextTrackPath => _next.Path;

    /// <summary>State of the single pending next-track preload slot.</summary>
    public NextTrackState NextTrackState => _next.State;

    /// <summary>Active track duration when known (updates at the consumption handoff).</summary>
    public TimeSpan Duration => _duration;

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
    /// Cleared by <see cref="Stop"/>, <see cref="LoadAsync"/>, and <see cref="Dispose"/>.
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

    /// <summary>Linear gain in the range [0, 1].</summary>
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
    /// The switch was superseded by <see cref="Stop"/>, <see cref="LoadAsync"/>, <see cref="Dispose"/>,
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

            oldOutput = DetachOutput_NoLock();
            _outputDeviceStatus = OutputDeviceStatus.Switching;
            RaiseOutputDeviceChanged(OutputDeviceChangeReason.Switching);
        }
        finally
        {
            _gate.Release();
        }

        // Yield so callers can observe Switching / supersede this switch before teardown blocks
        // the calling thread (Shutdown may wait for a live pull consumer).
        await Task.Yield();

        // Join previous output outside the gate so pull-loop wait cannot deadlock.
        try
        {
            ShutdownAndDispose(oldOutput);
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

            if (!_hasSource)
            {
                _activeOutputDeviceId = null;
                _effectiveOutputMode = null;
                _outputFallbackReason = ExclusiveFallbackReason.None;
                _outputDeviceStatus = OutputDeviceStatus.Available;
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
                noSourceConfigured = true;
                return;
            }

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
        finally
        {
            _gate.Release();
        }

        if (modeUnchanged || noSourceConfigured)
        {
            RaiseOutputConfigurationChanged();
            return;
        }

        await Task.Yield();

        try
        {
            ShutdownAndDispose(oldOutput);
        }
        catch (Exception ex)
        {
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
            RaiseOutputConfigurationChanged();
            throw;
        }

        ThrowIfSwitchSuperseded(switchGeneration, switchToken);

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
    public async Task LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var generation = 0;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_generation;
            CancelOutputSwitch_NoLock();
            _outputRecoveryIntent = OutputRecoveryIntent.None;
            _next.Clear();
            SetState_NoLock(PlaybackState.Buffering);

            await TearDownSession_NoLockAsync().ConfigureAwait(false);

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        IAudioDecoder decoder;
        try
        {
            decoder = await _decoderFactory.CreateAsync(filePath, cancellationToken).ConfigureAwait(false);
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
                PrepareSessionPipelines_NoLock(filePath, decoder);
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
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (generation == _generation && !_disposed)
                {
                    await TearDownSession_NoLockAsync().ConfigureAwait(false);
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
            created?.Dispose();
            _gate.Release();
        }

        RaiseOutputConfigurationChanged();
    }

    /// <summary>
    /// Preloads exactly one upcoming track without interrupting current playback.
    /// Replaces any previous preload. Failures set <see cref="NextTrackState.Failed"/> and do not stop the current track.
    /// Decoded PCM is converted to the session format and buffered with a bounded ring.
    /// </summary>
    public async Task PreloadNextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
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
            preloadGeneration = _next.BeginLoad(filePath);
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
            opened = await _decoderFactory.CreateAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (!_next.IsCurrentGeneration(preloadGeneration))
            {
                opened.Dispose();
                return;
            }

            var converted = _formatConverter.Convert(opened, sessionFormat);
            opened = null; // ownership transferred to converted (or same instance)
            pipeline = TrackDecodePipeline.Create(filePath, converted, _options);
            pipeline.StartPump(
                _options.Processors,
                () => Volatile.Read(ref _volume),
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

            StopTransport_NoLock(resetToStart: true, raiseCompletion: false);
            if (_output is null)
            {
                _outputDeviceStatus = OutputDeviceStatus.Unavailable;
            }
        }
        finally
        {
            _gate.Release();
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
    public AudioSeekResult SeekWithResult(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);

        _gate.Wait();
        try
        {
            return SeekWithResult_NoGate(position);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AudioSeekResult SeekWithResult_NoGate(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TrackDecodePipeline active;
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

        if (position > _duration && _duration > TimeSpan.Zero)
        {
            position = _duration;
        }

        // Invalidate in-flight transition notifications from a pre-seek drain.
        _activeTrackGeneration++;
        _suppressTransitionIdsAtOrBelow = Volatile.Read(ref _transitionId);

        var wasPlaying = _state == PlaybackState.Playing;
        var wasPaused = _state == PlaybackState.Paused;
        var previousHeld = _heldPosition;

        try
        {
            _output?.Pause();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            // Continue — barrier still protects DSP reset.
        }

        // Ensure no output callback can observe effect state while it is reset (no WASAPI Shutdown).
        _sampleSource?.EnterEffectsBarrier(TimeSpan.FromSeconds(2));

        AudioSeekResult result;
        try
        {
            lock (_pipelineSync)
            {
                // Re-validate: gapless promotion may have swapped the active track.
                if (_active is null)
                {
                    throw new InvalidOperationException("No audio source is loaded.");
                }

                active = _active;
                if (!active.Decoder.CanSeek)
                {
                    throw new NotSupportedException("The current audio source does not support seeking.");
                }

                var duration = active.Duration > TimeSpan.Zero ? active.Duration : _duration;
                if (position > duration && duration > TimeSpan.Zero)
                {
                    position = duration;
                }

                active.StopPump();
                active.Ring.Clear();
                _sampleSource?.ResetEndOfStream();
                result = active.Decoder.Seek(position);
            }

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
        }
        catch
        {
            // Leave a documented consistent state after a failed seek.
            try
            {
                if (_active is not null)
                {
                    _active.Ring.Clear();
                    _sampleSource?.ResetEndOfStream();
                    EnsureActivePumpRunning_NoLock();
                }

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
            catch
            {
                // Preserve the original seek failure.
            }

            throw;
        }
        finally
        {
            _sampleSource?.ExitEffectsBarrier();
        }

        EnsureActivePumpRunning_NoLock();
        if (wasPlaying && _output is not null)
        {
            active.Prime(_options.PrimeDuration);
            _output.Play();
            SetState_NoLock(PlaybackState.Playing);
        }
        else if (wasPaused)
        {
            SetState_NoLock(PlaybackState.Paused);
        }

        return result.EnsureInvariants();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            CancelOutputSwitch_NoLock();
            _outputRecoveryIntent = OutputRecoveryIntent.None;
            _outputDevices.DevicesChanged -= OnDevicesChanged;
            _outputDevices.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            _next.Clear();
            TearDownSession_NoLockAsync().GetAwaiter().GetResult();
            _effects.Dispose();
            SetState_NoLock(PlaybackState.Stopped);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void PrepareSessionPipelines_NoLock(string path, IAudioDecoder decoder)
    {
        _sessionFormat = decoder.Format;
        Volatile.Write(ref _sourcePath, path);
        _duration = decoder.Duration;
        _timelineOriginSamples = 0;
        Interlocked.Exchange(ref _samplesDecoded, 0);
        Interlocked.Exchange(ref _samplesConsumed, 0);
        _completionRaised = false;
        Interlocked.Exchange(ref _underrunCount, 0);
        Interlocked.Exchange(ref _underrunLatched, 0);
        _logicalPositionAtAnchor = TimeSpan.Zero;
        _deviceClockAnchor = TimeSpan.Zero;
        _heldPosition = TimeSpan.Zero;
        _activeTrackGeneration++;
        _suppressTransitionIdsAtOrBelow = _transitionId;

        _active = TrackDecodePipeline.Create(path, decoder, _options);

        _effects.Configure(decoder.Format);
        _effects.ResetProcessingState();
        _effects.DisposeRetired();

        _sampleSource = new TransitionSampleSource(
            decoder.Format,
            _effects,
            onSamplesConsumed: n =>
            {
                Interlocked.Add(ref _samplesConsumed, n);
                Interlocked.Exchange(ref _underrunLatched, 0);
            },
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

        try
        {
            output.Pause();
        }
        catch
        {
            // ignored
        }

        _output = null;
        _effectiveOutputMode = null;
        Volatile.Write(ref _activeOutputDeviceId, null);
        return output;
    }

    /// <summary>
    /// Guarantees the output will not read PCM again, then disposes it.
    /// Throws if quiescence cannot be guaranteed; Dispose is still attempted.
    /// </summary>
    private static void ShutdownAndDispose(IAudioOutput? output)
    {
        if (output is null)
        {
            return;
        }

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

        _output = output;
        _output.Stopped += OnOutputStopped;
        _output.Initialize(_sampleSource!);
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
            Volatile.Write(ref _sourcePath, nextPath);
            _duration = nextPipeline.Duration;
            _timelineOriginSamples = 0;
            Interlocked.Exchange(ref _samplesDecoded, 0);
            Interlocked.Exchange(ref _samplesConsumed, 0);
            Interlocked.Exchange(ref _underrunLatched, 0);

            // Rebase logical timeline to the start of B using the continuous device clock.
            _logicalPositionAtAnchor = TimeSpan.Zero;
            _deviceClockAnchor = _output?.TryGetRenderedPosition() ?? TimeSpan.Zero;
            _heldPosition = TimeSpan.Zero;

            transitionId = Interlocked.Increment(ref _transitionId);
        }
        finally
        {
            Monitor.Exit(_pipelineSync);
        }

        var args = new TrackTransitionedEventArgs(previousPath, nextPath, previousDuration, transitionId);
        var toDispose = previous;
        _ = Task.Run(() =>
        {
            try
            {
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
            () => Volatile.Read(ref _volume),
            onSamplesWritten: n => Interlocked.Add(ref _samplesDecoded, n),
            onError: ex => RaiseError(ex));
    }

    private async Task TearDownSession_NoLockAsync()
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

        var oldOutput = DetachOutput_NoLock();
        // Best-effort during session teardown (Load/Dispose). Switch paths use ShutdownAndDispose off-gate.
        try
        {
            ShutdownAndDispose(oldOutput);
        }
        catch
        {
            try
            {
                oldOutput?.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        _active?.StopPump();

        _sampleSource = null;

        _active?.Dispose();
        _active = null;

        _sessionCts?.Dispose();
        _sessionCts = null;

        _effects.DisposeRetired();

        _hasSource = false;
        Volatile.Write(ref _sourcePath, null);
        _duration = TimeSpan.Zero;
        _heldPosition = TimeSpan.Zero;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void StopTransport_NoLock(bool resetToStart, bool raiseCompletion)
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

        if (resetToStart && _active is not null)
        {
            if (_active.Decoder.CanSeek)
            {
                var seekResult = _active.Decoder.Seek(TimeSpan.Zero);
                var origin = seekResult.ActualPosition ?? TimeSpan.Zero;
                _timelineOriginSamples = _active.Format.TimeToSamples(origin);
            }
            else
            {
                _timelineOriginSamples = 0;
            }

            Interlocked.Exchange(ref _samplesDecoded, 0);
            Interlocked.Exchange(ref _samplesConsumed, 0);
            RebasePositionClock_NoLock(TimeSpan.Zero);
            EnsureActivePumpRunning_NoLock();
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
    }

    private void OnOutputStopped(object? sender, AudioOutputStoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            RaiseError(e.Exception);
        }

        if (!_gate.Wait(0))
        {
            _ = Task.Run(() => HandleOutputStopped(e));
            return;
        }

        try
        {
            HandleOutputStopped_NoLock(e);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void HandleOutputStopped(AudioOutputStoppedEventArgs e)
    {
        _gate.Wait();
        try
        {
            HandleOutputStopped_NoLock(e);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void HandleOutputStopped_NoLock(AudioOutputStoppedEventArgs e)
    {
        if (_disposed || !_hasSource)
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
                StopTransport_NoLock(resetToStart: true, raiseCompletion: true);
            }

            return;
        }

        if (e.Exception is null)
        {
            return;
        }

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
            _outputDeviceStatus = OutputDeviceStatus.Switching;
            SetState_NoLock(PlaybackState.Stopped);
            // Shutdown off the gate path: schedule after release via recovery / background.
            _ = Task.Run(() =>
            {
                try
                {
                    ShutdownAndDispose(lostOutput);
                }
                catch (Exception ex)
                {
                    RaiseError(ex);
                }
            });
            ScheduleDeviceRecovery(OutputDeviceChangeReason.DeviceLostFallback);
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
        int switchGeneration;
        CancellationToken switchToken;
        TimeSpan heldPosition;
        string? effectiveId;
        string? preferred;
        var intent = OutputRecoveryIntent.None;
        var needsCreate = false;
        IAudioOutput? oldOutput = null;
        try
        {
            if (_disposed || !_hasSource)
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
                oldOutput = DetachOutput_NoLock();
                _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                SetState_NoLock(PlaybackState.Stopped);
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
                return;
            }

            if (_output is not null
                && string.Equals(_activeOutputDeviceId, effectiveId, StringComparison.Ordinal)
                && _outputDeviceStatus is OutputDeviceStatus.Available or OutputDeviceStatus.PreferredUnavailable)
            {
                return;
            }

            BeginOutputSwitch_NoLock(CancellationToken.None, out switchGeneration, out switchToken);
            oldOutput = DetachOutput_NoLock();
            _outputDeviceStatus = OutputDeviceStatus.Switching;
            RaiseOutputDeviceChanged(OutputDeviceChangeReason.Switching);
            needsCreate = true;
        }
        finally
        {
            _gate.Release();
        }

        await Task.Yield();

        try
        {
            ShutdownAndDispose(oldOutput);
        }
        catch (Exception ex)
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!_disposed && switchGeneration == _outputSwitchGeneration)
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

            AttachOutput_NoLock(created, heldPosition, _outputConfiguration.Mode);
            created = null;

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

    private TimeSpan EstimateAudiblePosition()
    {
        if (_active is null)
        {
            return TimeSpan.Zero;
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

        if (_duration > TimeSpan.Zero && value > _duration)
        {
            return _duration;
        }

        return value;
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
                        CompleteRebindTransport_NoLock(
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

                CompleteRebindTransport_NoLock(
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

        if (raiseConfigurationChanged
            || requestedMode == AudioOutputMode.Exclusive
            || pendingFallbackReason != ExclusiveFallbackReason.None)
        {
            RaiseOutputConfigurationChanged();
        }
    }

    private void CompleteRebindTransport_NoLock(
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

        if (wasPlaying || _outputRecoveryIntent == OutputRecoveryIntent.ResumePlaying)
        {
            EnsureActivePumpRunning_NoLock();
            _active?.Prime(_options.PrimeDuration);
            _completionRaised = false;
            Volatile.Write(ref _acceptEndOfStreamCompletion, 1);
            SetState_NoLock(PlaybackState.Playing);
            _output!.Play();
            _outputRecoveryIntent = OutputRecoveryIntent.None;
        }
        else if (wasPaused || _outputRecoveryIntent == OutputRecoveryIntent.RemainPaused)
        {
            SetState_NoLock(PlaybackState.Paused);
            _outputRecoveryIntent = OutputRecoveryIntent.None;
        }
        else
        {
            SetState_NoLock(PlaybackState.Stopped);
        }

        RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
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
        string? nextPath = null;
        if (_next.State is NextTrackState.Ready or NextTrackState.Loading or NextTrackState.Failed)
        {
            nextPath = _next.Path;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        string? activePath;
        try
        {
            ThrowIfSwitchSuperseded(switchGeneration, switchToken);
            if (!_hasSource || _active is null)
            {
                throw CreateSupersededException();
            }

            activePath = _sourcePath ?? _active.Path;
            _heldPosition = heldPosition;

            // Tear down active pipeline but keep session flags for rebuild.
            _active.StopPump();
            _sampleSource = null;
            var oldActive = _active;
            _active = null;
            oldActive.Dispose();

            if (nextPath is not null)
            {
                // Path retained; pipeline disposed — explicit Failed until rebuilt.
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
            opened = await _decoderFactory.CreateAsync(activePath!, switchToken).ConfigureAwait(false);
            var converted = _formatConverter.Convert(opened, requiredFormat);
            opened = null;
            pipeline = TrackDecodePipeline.Create(activePath!, converted, _options);
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
                Volatile.Write(ref _sourcePath, activePath);
                _duration = pipeline.Duration;
                _timelineOriginSamples = requiredFormat.TimeToSamples(rebuildOrigin);
                Interlocked.Exchange(ref _samplesDecoded, 0);
                Interlocked.Exchange(ref _samplesConsumed, 0);
                _completionRaised = false;
                Interlocked.Exchange(ref _underrunLatched, 0);
                _activeTrackGeneration++;
                _suppressTransitionIdsAtOrBelow = _transitionId;

                _active = pipeline;
                pipeline = null;

                _effects.Configure(requiredFormat);
                _effects.ResetProcessingState();
                _effects.DisposeRetired();

                _sampleSource = new TransitionSampleSource(
                    requiredFormat,
                    _effects,
                    onSamplesConsumed: n =>
                    {
                        Interlocked.Add(ref _samplesConsumed, n);
                        Interlocked.Exchange(ref _underrunLatched, 0);
                    },
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

                _heldPosition = heldPosition;
                RebasePositionClock_NoLock(heldPosition);
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

        // Rebuild next track when path is known.
        if (nextPath is not null
            && _next.TryBeginRebuild(out var nextGeneration, out var rebuildPath))
        {
            IAudioDecoder? nextOpened = null;
            TrackDecodePipeline? nextPipeline = null;
            try
            {
                nextOpened = await _decoderFactory.CreateAsync(rebuildPath, switchToken).ConfigureAwait(false);
                var nextConverted = _formatConverter.Convert(nextOpened, requiredFormat);
                nextOpened = null;
                nextPipeline = TrackDecodePipeline.Create(rebuildPath, nextConverted, _options);
                nextPipeline.StartPump(
                    _options.Processors,
                    () => Volatile.Read(ref _volume),
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
