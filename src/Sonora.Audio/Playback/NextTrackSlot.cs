using Sonora.Audio.Formats;

namespace Sonora.Audio.Playback;

/// <summary>
/// Single pending next-track slot with generation-guarded replace/cancel.
/// </summary>
internal sealed class NextTrackSlot
{
    private readonly object _sync = new();
    private int _generation;
    private NextTrackState _state = NextTrackState.None;
    private string? _path;
    private TrackDecodePipeline? _pipeline;
    private Exception? _failure;

    public NextTrackState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public string? Path
    {
        get
        {
            lock (_sync)
            {
                return _path;
            }
        }
    }

    public TrackDecodePipeline? ReadyPipeline
    {
        get
        {
            lock (_sync)
            {
                return _state == NextTrackState.Ready ? _pipeline : null;
            }
        }
    }

    public int BeginLoad(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        int generation;
        TrackDecodePipeline? old;
        lock (_sync)
        {
            _generation++;
            generation = _generation;
            old = _pipeline;
            _pipeline = null;
            _path = path;
            _failure = null;
            _state = NextTrackState.Loading;
        }

        old?.Dispose();
        return generation;
    }

    public bool TryCompleteReady(int generation, TrackDecodePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        TrackDecodePipeline? obsolete = null;
        lock (_sync)
        {
            if (generation != _generation || _state != NextTrackState.Loading)
            {
                obsolete = pipeline;
            }
            else
            {
                obsolete = _pipeline;
                _pipeline = pipeline;
                _state = NextTrackState.Ready;
                _failure = null;
            }
        }

        obsolete?.Dispose();
        return obsolete != pipeline;
    }

    public bool TryCompleteFailed(int generation, Exception error)
    {
        lock (_sync)
        {
            if (generation != _generation || _state != NextTrackState.Loading)
            {
                return false;
            }

            _pipeline?.Dispose();
            _pipeline = null;
            _state = NextTrackState.Failed;
            _failure = error;
            return true;
        }
    }

    /// <summary>
    /// Atomically takes the ready pipeline for promotion, leaving the slot empty.
    /// </summary>
    public bool TryTakeReady(out TrackDecodePipeline pipeline, out string path)
    {
        lock (_sync)
        {
            if (_state != NextTrackState.Ready || _pipeline is null)
            {
                pipeline = null!;
                path = null!;
                return false;
            }

            pipeline = _pipeline;
            path = _path ?? pipeline.Path;
            _pipeline = null;
            _path = null;
            _state = NextTrackState.None;
            _failure = null;
            _generation++; // invalidate in-flight loads
            return true;
        }
    }

    public void Clear()
    {
        TrackDecodePipeline? old;
        lock (_sync)
        {
            _generation++;
            old = _pipeline;
            _pipeline = null;
            _path = null;
            _failure = null;
            _state = NextTrackState.None;
        }

        old?.Dispose();
    }

    /// <summary>
    /// Disposes a ready/loading pipeline but keeps <see cref="Path"/> and sets <see cref="NextTrackState.Failed"/>
    /// so callers can observe invalidation and retry preload after a session-format change.
    /// </summary>
    public void InvalidateKeepPath(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        TrackDecodePipeline? old;
        lock (_sync)
        {
            if (_state is NextTrackState.None || string.IsNullOrEmpty(_path))
            {
                return;
            }

            _generation++;
            old = _pipeline;
            _pipeline = null;
            _failure = error;
            _state = NextTrackState.Failed;
        }

        old?.Dispose();
    }

    /// <summary>Retains path and moves a ready/failed slot back to loading for an in-place rebuild.</summary>
    public bool TryBeginRebuild(out int generation, out string path)
    {
        TrackDecodePipeline? old;
        lock (_sync)
        {
            if (_path is null || _state is NextTrackState.None)
            {
                generation = 0;
                path = null!;
                return false;
            }

            _generation++;
            generation = _generation;
            path = _path;
            old = _pipeline;
            _pipeline = null;
            _failure = null;
            _state = NextTrackState.Loading;
        }

        old?.Dispose();
        return true;
    }

    public bool IsCurrentGeneration(int generation)
    {
        lock (_sync)
        {
            return generation == _generation;
        }
    }
}
