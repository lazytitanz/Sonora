using Sonora.Audio.Abstractions;
using Sonora.Audio.Devices;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;

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
    private OutputDeviceStatus _outputDeviceStatus = OutputDeviceStatus.Available;
    private OutputRecoveryIntent _outputRecoveryIntent = OutputRecoveryIntent.None;
    private int _recoveryInFlight;

    private long _timelineOriginSamples;
    private long _samplesDecoded;
    private long _samplesConsumed;
    private TimeSpan _heldPosition;
    private TimeSpan _duration;
    private TimeSpan _deviceClockAnchor;
    private TimeSpan _logicalPositionAtAnchor;

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

    /// <summary>
    /// Estimated audible playback position within the active track. Accounts for device rendered clock
    /// and output latency when available. This is an estimate and is not sample-accurate under shared-mode
    /// resampling, device buffering, underruns, gapless handoffs, or output device switches.
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
                ? _preferredOutputDeviceId is null && _outputDeviceStatus == OutputDeviceStatus.Available && _output is not null
                : string.Equals(_activeOutputDeviceId, deviceId, StringComparison.Ordinal)
                  && _outputDeviceStatus == OutputDeviceStatus.Available
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

            oldOutput = DetachOutput_NoLock();
        }
        finally
        {
            _gate.Release();
        }

        // Dispose previous output outside the gate so pull-loop join cannot deadlock.
        DisposeOutputSafe(oldOutput);

        IAudioOutput? created = null;
        try
        {
            created = await _outputFactory.CreateOutputAsync(effectiveDeviceId, switchToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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

                _activeOutputDeviceId = null;
                _outputDeviceStatus = OutputDeviceStatus.Unavailable;
                if (wasPlaying)
                {
                    _outputRecoveryIntent = OutputRecoveryIntent.ResumePlaying;
                }
                else if (wasPaused)
                {
                    _outputRecoveryIntent = OutputRecoveryIntent.RemainPaused;
                }

                _heldPosition = heldPosition;
                SetState_NoLock(PlaybackState.Stopped);
                RaiseError(ex is AudioException ? ex : new AudioOutputException("Failed to open the output device.", ex));
                RaiseOutputDeviceChanged(OutputDeviceChangeReason.Unavailable);
            }
            finally
            {
                _gate.Release();
            }

            throw;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed || switchGeneration != _outputSwitchGeneration)
            {
                created.Dispose();
                return;
            }

            if (!_hasSource || _sampleSource is null)
            {
                created.Dispose();
                return;
            }

            // Seek may have advanced the timeline while create was off-gate.
            var attachPosition = _state is PlaybackState.Playing
                ? EstimateAudiblePosition()
                : (_heldPosition != TimeSpan.Zero ? _heldPosition : heldPosition);
            if (_heldPosition != heldPosition && _state is PlaybackState.Stopped or PlaybackState.Paused)
            {
                attachPosition = _heldPosition;
            }

            AttachOutput_NoLock(created, attachPosition);
            created = null;

            _outputDeviceStatus = deviceId is not null
                && !string.Equals(_activeOutputDeviceId, deviceId, StringComparison.Ordinal)
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

            RaiseOutputDeviceChanged(OutputDeviceChangeReason.SelectionChanged);
        }
        finally
        {
            created?.Dispose();
            _gate.Release();
        }
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
            created = await _outputFactory.CreateOutputAsync(effectiveId, switchToken).ConfigureAwait(false);
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

            AttachOutput_NoLock(created, TimeSpan.Zero);
            created = null;
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
        }
        finally
        {
            created?.Dispose();
            _gate.Release();
        }
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

            if (_output is null || _outputDeviceStatus == OutputDeviceStatus.Unavailable)
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
                SetState_NoLock(PlaybackState.Stopped);
                return;
            }

            StopTransport_NoLock(resetToStart: true, raiseCompletion: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Seeks within the active track. Preserves a safe preloaded next track; does not trigger a transition.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);

        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is null)
            {
                throw new InvalidOperationException("No audio source is loaded.");
            }

            if (!_active.Decoder.CanSeek)
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
            _output?.Pause();

            lock (_pipelineSync)
            {
                _active.StopPump();
                _active.Ring.Clear();
                _sampleSource?.ResetEndOfStream();
                _active.Decoder.Seek(position);
            }

            _effects.ResetProcessingState();
            _effects.DisposeRetired();

            _timelineOriginSamples = _active.Format.TimeToSamples(position);
            Interlocked.Exchange(ref _samplesDecoded, 0);
            Interlocked.Exchange(ref _samplesConsumed, 0);
            _completionRaised = false;
            Interlocked.Exchange(ref _underrunLatched, 0);
            RebasePositionClock_NoLock(position);

            EnsureActivePumpRunning_NoLock();
            if (wasPlaying && _output is not null)
            {
                _active.Prime(_options.PrimeDuration);
                _output.Play();
                SetState_NoLock(PlaybackState.Playing);
            }
        }
        finally
        {
            _gate.Release();
        }
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
        Volatile.Write(ref _activeOutputDeviceId, null);
        return output;
    }

    private static void DisposeOutputSafe(IAudioOutput? output)
    {
        if (output is null)
        {
            return;
        }

        try
        {
            output.Stop();
        }
        catch
        {
            // ignored
        }

        try
        {
            output.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    private void AttachOutput_NoLock(IAudioOutput output, TimeSpan logicalPosition)
    {
        _effects.ResetProcessingState();
        _effects.DisposeRetired();
        _sampleSource?.ResetEndOfStream();

        _output = output;
        _output.Stopped += OnOutputStopped;
        _output.Initialize(_sampleSource!);
        Volatile.Write(ref _activeOutputDeviceId, output.EndpointId);
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
        // Teardown already holds the gate; Stop/Dispose must not re-enter player callbacks.
        DisposeOutputSafe(oldOutput);

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
                _active.Decoder.Seek(TimeSpan.Zero);
            }

            _timelineOriginSamples = 0;
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
            SetState_NoLock(PlaybackState.Stopped);
            // Dispose off the gate path: schedule after release via recovery / background.
            _ = Task.Run(() => DisposeOutputSafe(lostOutput));
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
                && _outputDeviceStatus != OutputDeviceStatus.Unavailable)
            {
                return;
            }

            BeginOutputSwitch_NoLock(CancellationToken.None, out switchGeneration, out switchToken);
            oldOutput = DetachOutput_NoLock();
            needsCreate = true;
        }
        finally
        {
            _gate.Release();
        }

        DisposeOutputSafe(oldOutput);

        if (!needsCreate)
        {
            return;
        }

        IAudioOutput? created = null;
        try
        {
            created = await _outputFactory.CreateOutputAsync(effectiveId, switchToken).ConfigureAwait(false);
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

            AttachOutput_NoLock(created, heldPosition);
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
}
