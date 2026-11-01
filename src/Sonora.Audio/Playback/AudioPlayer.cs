using Sonora.Audio.Abstractions;
using Sonora.Audio.Buffers;
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
    private readonly AudioEngineOptions _options;
    private readonly AudioEffectRack _effects;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PlaybackState _state = PlaybackState.Stopped;
    private float _volume = 1f;
    private int _generation;
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

    private IAudioDecoder? _decoder;
    private IAudioOutput? _output;
    private PcmRingBuffer? _ring;
    private DecodePump? _pump;
    private BufferedSampleSource? _sampleSource;
    private CancellationTokenSource? _sessionCts;

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
        AudioEngineOptions? options = null)
    {
        _decoderFactory = decoderFactory ?? throw new ArgumentNullException(nameof(decoderFactory));
        _outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));
        _options = options ?? new AudioEngineOptions();
        _effects = new AudioEffectRack(_options.EffectFactory);
    }

    /// <summary>
    /// Per-player DSP effect rack (equalizer, preamp, limiter). Processing runs after the PCM ring
    /// on the output path so live parameter changes apply without waiting for buffered audio to drain.
    /// Effect settings are preserved across track changes; filter state is reset on seek/stop/load.
    /// </summary>
    public IAudioEffectRack Effects => _effects;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised exactly once when playback reaches the natural end of the current source.</summary>
    public event EventHandler? PlaybackCompleted;

    /// <summary>Raised when a recoverable or fatal playback error occurs.</summary>
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>Raised once per underrun episode while playing.</summary>
    public event EventHandler? Underrun;

    /// <summary>Current playback state.</summary>
    public PlaybackState State => _state;

    /// <summary>Whether a source is loaded.</summary>
    public bool HasSource => Volatile.Read(ref _hasSource);

    /// <summary>Source duration when known.</summary>
    public TimeSpan Duration => _duration;

    /// <summary>
    /// Estimated audible playback position. Accounts for buffered samples handed to the device and output latency when available.
    /// This is an estimate and is not sample-accurate under shared-mode resampling or underruns.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            if (!_hasSource || _decoder is null)
            {
                return TimeSpan.Zero;
            }

            var state = _state;
            if (state is PlaybackState.Stopped)
            {
                return _heldPosition;
            }

            if (state is PlaybackState.Paused)
            {
                return _heldPosition;
            }

            return EstimateAudiblePosition();
        }
    }

    /// <summary>
    /// Timeline position of the latest sample written into the PCM ring buffer (decode frontier).
    /// </summary>
    public TimeSpan BufferedPosition
    {
        get
        {
            if (!_hasSource || _decoder is null)
            {
                return TimeSpan.Zero;
            }

            var samples = _timelineOriginSamples + Interlocked.Read(ref _samplesDecoded);
            return ClampToDuration(_decoder.Format.SamplesToTime(samples));
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
            BufferCapacitySamples = _ring?.Capacity ?? 0,
            BufferedSamples = _ring?.Count ?? 0,
        };
    }

    /// <summary>
    /// Loads an audio file, releasing any previously loaded source.
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
            SetState_NoLock(PlaybackState.Buffering);

            await TearDownSession_NoLockAsync().ConfigureAwait(false);

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sessionToken = _sessionCts.Token;

            // Release the gate while opening the decoder so Play/Dispose can still serialize afterwards via generation checks.
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
                StartSession_NoLock(decoder);
                SetState_NoLock(PlaybackState.Stopped);
            }
            catch
            {
                decoder.Dispose();
                SetState_NoLock(PlaybackState.Stopped);
                throw;
            }
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
            if (!_hasSource || _output is null || _ring is null || _decoder is null)
            {
                throw new InvalidOperationException("No audio source is loaded.");
            }

            if (_state == PlaybackState.Playing)
            {
                return;
            }

            EnsurePumpRunning_NoLock();
            PrimeBuffer_NoLock();
            _completionRaised = false;
            Volatile.Write(ref _acceptEndOfStreamCompletion, 1);
            // Publish Playing before starting the device so callback underrun detection sees the active state.
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

    /// <summary>Stops playback and resets position to the beginning without raising <see cref="PlaybackCompleted"/>.</summary>
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    /// <summary>Seeks to <paramref name="position"/> when the source supports seeking.</summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);

        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_decoder is null || _ring is null)
            {
                throw new InvalidOperationException("No audio source is loaded.");
            }

            if (!_decoder.CanSeek)
            {
                throw new NotSupportedException("The current audio source does not support seeking.");
            }

            if (position > _duration && _duration > TimeSpan.Zero)
            {
                position = _duration;
            }

            var wasPlaying = _state == PlaybackState.Playing;
            _output?.Pause();

            StopPump_NoLock();
            _ring.Clear();
            _sampleSource?.ResetEndOfStream();
            _decoder.Seek(position);
            // Output is paused: safe to reset IIR/dynamics state and dispose removed effects.
            _effects.ResetProcessingState();
            _effects.DisposeRetired();

            _timelineOriginSamples = _decoder.Format.TimeToSamples(position);
            Interlocked.Exchange(ref _samplesDecoded, 0);
            Interlocked.Exchange(ref _samplesConsumed, 0);
            _completionRaised = false;
            Interlocked.Exchange(ref _underrunLatched, 0);
            RebasePositionClock_NoLock(position);

            EnsurePumpRunning_NoLock();
            if (wasPlaying)
            {
                PrimeBuffer_NoLock();
                _output?.Play();
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

    private void StartSession_NoLock(IAudioDecoder decoder)
    {
        _decoder = decoder;
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

        var capacity = Math.Max(
            decoder.Format.ChannelCount * 256,
            (int)Math.Ceiling(_options.BufferDuration.TotalSeconds * decoder.Format.SamplesPerSecond));

        _ring = new PcmRingBuffer(capacity);
        // Preserve effect instances/settings across tracks; reconfigure + reset processing state only.
        _effects.Configure(decoder.Format);
        _effects.ResetProcessingState();
        _effects.DisposeRetired();

        _sampleSource = new BufferedSampleSource(
            _ring,
            decoder.Format,
            _effects,
            onSamplesConsumed: n =>
            {
                Interlocked.Add(ref _samplesConsumed, n);
                Interlocked.Exchange(ref _underrunLatched, 0);
            },
            onUnderrun: HandleUnderrunFromCallback,
            isPlaying: () => _state == PlaybackState.Playing);

        _output = _outputFactory.CreateDefaultOutput();
        _output.Stopped += OnOutputStopped;
        _output.Initialize(_sampleSource);

        _hasSource = true;
        EnsurePumpRunning_NoLock();
        PrimeBuffer_NoLock();
    }

    private void EnsurePumpRunning_NoLock()
    {
        if (_decoder is null || _ring is null)
        {
            return;
        }

        if (_pump?.IsRunning == true)
        {
            return;
        }

        _pump?.Dispose();
        _pump = new DecodePump(
            _decoder,
            _ring,
            _options.Processors,
            () => Volatile.Read(ref _volume),
            onSamplesWritten: n => Interlocked.Add(ref _samplesDecoded, n),
            onError: ex => RaiseError(ex));
        _pump.Start();
    }

    private void PrimeBuffer_NoLock()
    {
        if (_ring is null || _decoder is null)
        {
            return;
        }

        var target = (int)Math.Ceiling(_options.PrimeDuration.TotalSeconds * _decoder.Format.SamplesPerSecond);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_ring.Count < target && !_ring.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(5);
        }
    }

    private void StopPump_NoLock()
    {
        var pump = _pump;
        _pump = null;
        if (pump is null)
        {
            return;
        }

        pump.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        pump.Dispose();
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

        if (_output is not null)
        {
            try
            {
                _output.Stopped -= OnOutputStopped;
                _output.Stop();
            }
            catch
            {
                // ignored during teardown
            }
        }

        StopPump_NoLock();

        _output?.Dispose();
        _output = null;

        _ring?.Dispose();
        _ring = null;
        _sampleSource = null;

        _decoder?.Dispose();
        _decoder = null;

        _sessionCts?.Dispose();
        _sessionCts = null;

        // Callback path is torn down: dispose effects removed while the previous session was active.
        _effects.DisposeRetired();

        _hasSource = false;
        _duration = TimeSpan.Zero;
        _heldPosition = TimeSpan.Zero;
    }

    private void StopTransport_NoLock(bool resetToStart, bool raiseCompletion)
    {
        // Suppress EOS completion before stopping the device — WASAPI/NAudio may raise
        // PlaybackStopped asynchronously (or synchronously) as a result of Stop().
        Volatile.Write(ref _acceptEndOfStreamCompletion, 0);

        _output?.Stop();
        StopPump_NoLock();

        if (_ring is not null)
        {
            _ring.Clear();
        }

        _sampleSource?.ResetEndOfStream();
        _effects.ResetProcessingState();
        _effects.DisposeRetired();

        if (resetToStart && _decoder is not null)
        {
            if (_decoder.CanSeek)
            {
                _decoder.Seek(TimeSpan.Zero);
            }

            _timelineOriginSamples = 0;
            Interlocked.Exchange(ref _samplesDecoded, 0);
            Interlocked.Exchange(ref _samplesConsumed, 0);
            RebasePositionClock_NoLock(TimeSpan.Zero);
            EnsurePumpRunning_NoLock();
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

        // Avoid taking the command gate on the output thread for long; try enter with timeout.
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
            // Ignore EOS after explicit Stop/teardown, or from a stale callback.
            if (Volatile.Read(ref _acceptEndOfStreamCompletion) == 0)
            {
                return;
            }

            if (_state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering)
            {
                StopTransport_NoLock(resetToStart: true, raiseCompletion: true);
            }
        }
        else if (e.Exception is not null && _state == PlaybackState.Playing)
        {
            Volatile.Write(ref _acceptEndOfStreamCompletion, 0);
            _heldPosition = EstimateAudiblePosition();
            SetState_NoLock(PlaybackState.Stopped);
        }
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
        if (_decoder is null)
        {
            return TimeSpan.Zero;
        }

        var rendered = _output?.TryGetRenderedPosition();
        if (rendered is not null)
        {
            // Rebase device clock against the last seek/load/stop so logical timeline stays correct.
            var delta = rendered.Value - _deviceClockAnchor;
            if (delta < TimeSpan.Zero)
            {
                delta = TimeSpan.Zero;
            }

            return ClampToDuration(_logicalPositionAtAnchor + delta);
        }

        var consumed = _timelineOriginSamples + Interlocked.Read(ref _samplesConsumed);
        var buffered = _ring?.Count ?? 0;
        var latencySamples = 0L;
        if (_output is not null)
        {
            latencySamples = _decoder.Format.TimeToSamples(_output.CurrentLatency);
        }

        var audibleSamples = Math.Max(0, consumed - buffered - latencySamples);
        return ClampToDuration(_decoder.Format.SamplesToTime(audibleSamples));
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
        // Raise after state publish; still under gate — consumers must not re-enter player commands.
        // Documented: re-entrancy into Play/Pause/Stop/Seek/Load/Dispose from event handlers is unsupported.
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
}
