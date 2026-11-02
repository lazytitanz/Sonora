namespace Sonora.TestPlayer.Controllers;

/// <summary>Why <see cref="QueueAdvanceCoordinator"/> did or did not advance the queue.</summary>
public enum QueueAdvanceReason
{
    /// <summary>The queue advanced (transition: index moved; completion: caller must load the next item).</summary>
    Advanced = 0,

    /// <summary>The event belongs to an older session (a Load or Stop happened since it was raised).</summary>
    StaleGeneration = 1,

    /// <summary>A completion for this session was already handled.</summary>
    DuplicateCompletion = 2,

    /// <summary>A gapless <c>TrackTransitioned</c> already advanced the queue for this completion cycle.</summary>
    AlreadyAdvancedViaTransition = 3,

    /// <summary>There is no queue item after the current one.</summary>
    NoMoreTracks = 4,

    /// <summary>The transition reported no next path.</summary>
    NoNextPath = 5,

    /// <summary>The transition's next path was not found after the current queue position.</summary>
    NextPathNotInQueue = 6,

    /// <summary>The same or an older transition id was already applied.</summary>
    DuplicateTransition = 7,
}

/// <summary>Decision returned by <see cref="QueueAdvanceCoordinator"/>.</summary>
/// <param name="ShouldAdvance">
/// For <see cref="QueueAdvanceCoordinator.OnTrackTransitioned"/>: the current index was moved to
/// <paramref name="NextIndex"/> and the player is already playing it (no load needed).
/// For <see cref="QueueAdvanceCoordinator.OnPlaybackCompleted"/>: the caller must load and play
/// <paramref name="NextIndex"/>.
/// </param>
/// <param name="NextIndex">Target queue index, or -1 when nothing advances.</param>
/// <param name="Reason">Explanation of the decision.</param>
/// <param name="Generation">Session generation at the time of the decision.</param>
public readonly record struct QueueAdvanceDecision(
    bool ShouldAdvance,
    int NextIndex,
    QueueAdvanceReason Reason,
    int Generation);

/// <summary>
/// Pure, hardware-free decision logic for advancing an application-owned queue in response to Sonora's
/// <c>TrackTransitioned</c> (gapless handoff) and <c>PlaybackCompleted</c> (natural end of the last buffered
/// track) events.
/// </summary>
/// <remarks>
/// <para><b>Rules</b></para>
/// <list type="bullet">
/// <item><description>Every Load/Stop starts a new <em>generation</em> (<see cref="BeginSession"/> /
/// <see cref="Invalidate"/>). Events carry the generation observed when they were raised; events from an older
/// generation are ignored, so late deliveries after a Load or Stop can never move the queue.</description></item>
/// <item><description><see cref="OnTrackTransitioned"/> moves the index to the transitioned path and marks the
/// current completion cycle as <em>already advanced via transition</em>.</description></item>
/// <item><description><see cref="OnPlaybackCompleted"/> asks the caller to load the next item only when more tracks
/// remain and the transition marker does not cover this completion. A completion arriving within
/// the duplicate window after a transition is treated as belonging to that transition and does not advance
/// again; a completion arriving later (the new track genuinely ended, for example because its successor failed to
/// preload) does advance.</description></item>
/// <item><description>A completion is handled at most once per generation.</description></item>
/// <item><description>Stop calls <see cref="Invalidate"/>, so it is never treated as a completion.</description></item>
/// </list>
/// <para>
/// Thread-safety: all members are safe to call from any thread; an internal lock guards the few fields and is
/// never held while calling out. The class performs no I/O and has no dependency on Sonora or WASAPI.
/// </para>
/// </remarks>
public sealed class QueueAdvanceCoordinator
{
    /// <summary>Default time after a transition during which a completion is considered a duplicate of it.</summary>
    public static readonly TimeSpan DefaultDuplicateCompletionWindow = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _duplicateWindow;
    private int _generation;
    private int _currentIndex = -1;
    private bool _advancedViaTransition;
    private long _transitionTimestamp;
    private int _lastTransitionId = int.MinValue;
    private bool _completionHandled;

    /// <summary>Creates a coordinator.</summary>
    /// <param name="duplicateCompletionWindow">
    /// Window after a transition in which a completion is deemed part of the same cycle. Defaults to
    /// <see cref="DefaultDuplicateCompletionWindow"/>.
    /// </param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>. Injectable for tests.</param>
    public QueueAdvanceCoordinator(TimeSpan? duplicateCompletionWindow = null, TimeProvider? timeProvider = null)
    {
        var window = duplicateCompletionWindow ?? DefaultDuplicateCompletionWindow;
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.Zero);
        _duplicateWindow = window;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Current session generation. Safe to read from any thread.</summary>
    public int Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    /// <summary>Index of the item considered current, or -1 when none (the next item is then index 0).</summary>
    public int CurrentIndex
    {
        get
        {
            lock (_sync)
            {
                return _currentIndex;
            }
        }
    }

    /// <summary>Whether <paramref name="generation"/> is still the active session.</summary>
    public bool IsCurrent(int generation)
    {
        lock (_sync)
        {
            return generation == _generation;
        }
    }

    /// <summary>
    /// Starts a new session for an explicit load/play of <paramref name="currentIndex"/> (or -1 for a source that is
    /// not in the queue). Invalidates all in-flight events and clears transition/completion markers.
    /// </summary>
    /// <returns>The new generation.</returns>
    public int BeginSession(int currentIndex)
    {
        lock (_sync)
        {
            _generation++;
            _currentIndex = Math.Max(-1, currentIndex);
            ResetCycle();
            return _generation;
        }
    }

    /// <summary>
    /// Invalidates in-flight events without changing the current index. Call on Stop (which must not
    /// behave like a completion), on Dispose, and before clearing the queue.
    /// </summary>
    /// <returns>The new generation.</returns>
    public int Invalidate()
    {
        lock (_sync)
        {
            _generation++;
            ResetCycle();
            return _generation;
        }
    }

    /// <summary>
    /// Re-bases the current index after the application edits the queue. Does not start a new session.
    /// </summary>
    public void SetCurrentIndex(int currentIndex)
    {
        lock (_sync)
        {
            _currentIndex = Math.Max(-1, currentIndex);
        }
    }

    /// <summary>Returns the index of the item that follows the current one, or <see langword="null"/>.</summary>
    public int? PeekNextIndex(int queueCount)
    {
        lock (_sync)
        {
            var next = _currentIndex + 1;
            return next >= 0 && next < queueCount ? next : null;
        }
    }

    /// <summary>
    /// Applies a gapless <c>TrackTransitioned</c> event: moves the current index to the transitioned path and marks
    /// the completion cycle as advanced via transition.
    /// </summary>
    /// <param name="eventGeneration">Generation observed when the event was raised.</param>
    /// <param name="transitionId">Engine transition id (monotonic per player).</param>
    /// <param name="nextPath">Path now playing, as reported by the engine.</param>
    /// <param name="queuePaths">Snapshot of the queue's paths, in order.</param>
    public QueueAdvanceDecision OnTrackTransitioned(
        int eventGeneration,
        int transitionId,
        string? nextPath,
        IReadOnlyList<string> queuePaths)
    {
        ArgumentNullException.ThrowIfNull(queuePaths);

        lock (_sync)
        {
            if (eventGeneration != _generation)
            {
                return Decision(false, -1, QueueAdvanceReason.StaleGeneration);
            }

            if (transitionId <= _lastTransitionId)
            {
                return Decision(false, -1, QueueAdvanceReason.DuplicateTransition);
            }

            if (string.IsNullOrEmpty(nextPath))
            {
                return Decision(false, -1, QueueAdvanceReason.NoNextPath);
            }

            var index = -1;
            for (var i = _currentIndex + 1; i < queuePaths.Count; i++)
            {
                if (string.Equals(queuePaths[i], nextPath, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return Decision(false, -1, QueueAdvanceReason.NextPathNotInQueue);
            }

            _lastTransitionId = transitionId;
            _currentIndex = index;
            _advancedViaTransition = true;
            _transitionTimestamp = _timeProvider.GetTimestamp();
            _completionHandled = false;
            return Decision(true, index, QueueAdvanceReason.Advanced);
        }
    }

    /// <summary>
    /// Applies a <c>PlaybackCompleted</c> event. When <see cref="QueueAdvanceDecision.ShouldAdvance"/> is true the
    /// caller must load and play <see cref="QueueAdvanceDecision.NextIndex"/> (via a new session).
    /// </summary>
    /// <param name="eventGeneration">Generation observed when the event was raised.</param>
    /// <param name="queueCount">Current number of queued items.</param>
    public QueueAdvanceDecision OnPlaybackCompleted(int eventGeneration, int queueCount)
    {
        lock (_sync)
        {
            if (eventGeneration != _generation)
            {
                return Decision(false, -1, QueueAdvanceReason.StaleGeneration);
            }

            if (_completionHandled)
            {
                return Decision(false, -1, QueueAdvanceReason.DuplicateCompletion);
            }

            if (_advancedViaTransition)
            {
                var elapsed = _timeProvider.GetElapsedTime(_transitionTimestamp);
                if (elapsed <= _duplicateWindow)
                {
                    // The completion belongs to the handoff that already advanced the queue. The marker stays
                    // set for the whole window so repeated deliveries of the same completion are all ignored.
                    return Decision(false, -1, QueueAdvanceReason.AlreadyAdvancedViaTransition);
                }

                // Window elapsed: the transitioned-to track has now genuinely finished.
                _advancedViaTransition = false;
            }

            _completionHandled = true;
            var next = _currentIndex + 1;
            if (next < 0 || next >= queueCount)
            {
                return Decision(false, -1, QueueAdvanceReason.NoMoreTracks);
            }

            return Decision(true, next, QueueAdvanceReason.Advanced);
        }
    }

    private QueueAdvanceDecision Decision(bool advance, int index, QueueAdvanceReason reason) =>
        new(advance, index, reason, _generation);

    private void ResetCycle()
    {
        _advancedViaTransition = false;
        _completionHandled = false;
    }
}
