using Sonora.TestPlayer.Controllers;

namespace Sonora.TestPlayer.Tests;

/// <summary>Hardware-free tests for TrackTransitioned vs PlaybackCompleted queue advancement.</summary>
public sealed class QueueAdvanceCoordinatorTests
{
    private static readonly string[] Queue = [@"C:\m\a.flac", @"C:\m\b.flac", @"C:\m\c.flac"];

    private readonly ManualTimeProvider _time = new();

    private QueueAdvanceCoordinator Create(TimeSpan? window = null) => new(window, _time);

    [Fact]
    public void Completion_with_more_tracks_asks_caller_to_load_next()
    {
        var c = Create();
        var gen = c.BeginSession(0);

        var d = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.True(d.ShouldAdvance);
        Assert.Equal(1, d.NextIndex);
        Assert.Equal(QueueAdvanceReason.Advanced, d.Reason);
    }

    [Fact]
    public void Completion_on_last_track_does_not_advance()
    {
        var c = Create();
        var gen = c.BeginSession(2);

        var d = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.False(d.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.NoMoreTracks, d.Reason);
        Assert.Equal(-1, d.NextIndex);
    }

    [Fact]
    public void Completion_with_empty_queue_does_not_advance()
    {
        var c = Create();
        var gen = c.BeginSession(-1);

        var d = c.OnPlaybackCompleted(gen, queueCount: 0);

        Assert.False(d.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.NoMoreTracks, d.Reason);
    }

    [Fact]
    public void Transition_moves_index_to_transitioned_path()
    {
        var c = Create();
        var gen = c.BeginSession(0);

        var d = c.OnTrackTransitioned(gen, transitionId: 1, Queue[1], Queue);

        Assert.True(d.ShouldAdvance);
        Assert.Equal(1, d.NextIndex);
        Assert.Equal(1, c.CurrentIndex);
    }

    [Fact]
    public void Completion_right_after_transition_does_not_advance_again()
    {
        var c = Create();
        var gen = c.BeginSession(0);
        c.OnTrackTransitioned(gen, 1, Queue[1], Queue);

        _time.Advance(TimeSpan.FromMilliseconds(20));
        var d = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.False(d.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.AlreadyAdvancedViaTransition, d.Reason);
        Assert.Equal(1, c.CurrentIndex);
    }

    [Fact]
    public void Repeated_completions_inside_the_window_are_all_ignored()
    {
        var c = Create();
        var gen = c.BeginSession(0);
        c.OnTrackTransitioned(gen, 1, Queue[1], Queue);

        _time.Advance(TimeSpan.FromMilliseconds(10));
        var first = c.OnPlaybackCompleted(gen, Queue.Length);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        var second = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.False(first.ShouldAdvance);
        Assert.False(second.ShouldAdvance);
        Assert.Equal(1, c.CurrentIndex);
    }

    [Fact]
    public void Completion_long_after_transition_is_a_genuine_end_and_advances()
    {
        // Gapless preload for the following track failed, so the transitioned-to track ends on its own.
        var c = Create();
        var gen = c.BeginSession(0);
        c.OnTrackTransitioned(gen, 1, Queue[1], Queue);

        _time.Advance(TimeSpan.FromSeconds(30));
        var d = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.True(d.ShouldAdvance);
        Assert.Equal(2, d.NextIndex);
    }

    [Fact]
    public void Full_gapless_run_advances_each_track_exactly_once()
    {
        var c = Create();
        var gen = c.BeginSession(0);
        var advances = 0;

        // a -> b -> c via transitions
        for (var id = 1; id <= 2; id++)
        {
            var t = c.OnTrackTransitioned(gen, id, Queue[id], Queue);
            Assert.True(t.ShouldAdvance);
            advances++;
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        // c ends naturally with nothing after it.
        var end = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.Equal(2, advances);
        Assert.Equal(2, c.CurrentIndex);
        Assert.False(end.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.NoMoreTracks, end.Reason);
    }

    [Fact]
    public void Completion_is_handled_once_per_generation()
    {
        var c = Create();
        var gen = c.BeginSession(0);

        var first = c.OnPlaybackCompleted(gen, Queue.Length);
        var second = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.True(first.ShouldAdvance);
        Assert.False(second.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.DuplicateCompletion, second.Reason);
    }

    [Fact]
    public void New_session_after_advance_can_complete_again()
    {
        var c = Create();
        var gen = c.BeginSession(0);
        var first = c.OnPlaybackCompleted(gen, Queue.Length);
        Assert.True(first.ShouldAdvance);

        var gen2 = c.BeginSession(first.NextIndex);
        var second = c.OnPlaybackCompleted(gen2, Queue.Length);

        Assert.True(second.ShouldAdvance);
        Assert.Equal(2, second.NextIndex);
    }

    [Fact]
    public void Events_from_a_previous_generation_are_ignored_after_load()
    {
        var c = Create();
        var oldGen = c.BeginSession(0);
        c.BeginSession(0); // user loaded another track

        var transition = c.OnTrackTransitioned(oldGen, 1, Queue[1], Queue);
        var completion = c.OnPlaybackCompleted(oldGen, Queue.Length);

        Assert.False(transition.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.StaleGeneration, transition.Reason);
        Assert.False(completion.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.StaleGeneration, completion.Reason);
        Assert.Equal(0, c.CurrentIndex);
    }

    [Fact]
    public void Stop_invalidates_pending_completion_so_it_never_advances()
    {
        var c = Create();
        var gen = c.BeginSession(0);

        c.Invalidate(); // explicit Stop
        var d = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.False(d.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.StaleGeneration, d.Reason);
        Assert.Equal(0, c.CurrentIndex);
    }

    [Fact]
    public void Stop_clears_transition_marker_and_keeps_index()
    {
        var c = Create();
        var gen = c.BeginSession(0);
        c.OnTrackTransitioned(gen, 1, Queue[1], Queue);

        var newGen = c.Invalidate();
        _time.Advance(TimeSpan.FromMilliseconds(5));
        var d = c.OnPlaybackCompleted(newGen, Queue.Length);

        // The marker was cleared by Stop, so a later genuine completion (e.g. replay of the stopped track) advances.
        Assert.True(d.ShouldAdvance);
        Assert.Equal(2, d.NextIndex);
    }

    [Fact]
    public void Duplicate_or_older_transition_ids_are_ignored()
    {
        var c = Create();
        var gen = c.BeginSession(0);
        Assert.True(c.OnTrackTransitioned(gen, 5, Queue[1], Queue).ShouldAdvance);

        var duplicate = c.OnTrackTransitioned(gen, 5, Queue[2], Queue);
        var older = c.OnTrackTransitioned(gen, 4, Queue[2], Queue);

        Assert.Equal(QueueAdvanceReason.DuplicateTransition, duplicate.Reason);
        Assert.Equal(QueueAdvanceReason.DuplicateTransition, older.Reason);
        Assert.Equal(1, c.CurrentIndex);
    }

    [Fact]
    public void Transition_to_unknown_or_missing_path_is_ignored()
    {
        var c = Create();
        var gen = c.BeginSession(0);

        var unknown = c.OnTrackTransitioned(gen, 1, @"C:\m\zzz.flac", Queue);
        var none = c.OnTrackTransitioned(gen, 2, null, Queue);

        Assert.Equal(QueueAdvanceReason.NextPathNotInQueue, unknown.Reason);
        Assert.Equal(QueueAdvanceReason.NoNextPath, none.Reason);
        Assert.Equal(0, c.CurrentIndex);
    }

    [Fact]
    public void Transition_only_searches_forward_from_current_position()
    {
        var c = Create();
        var gen = c.BeginSession(2);

        var d = c.OnTrackTransitioned(gen, 1, Queue[0], Queue);

        Assert.False(d.ShouldAdvance);
        Assert.Equal(QueueAdvanceReason.NextPathNotInQueue, d.Reason);
    }

    [Fact]
    public void Transition_path_match_is_case_insensitive()
    {
        var c = Create();
        var gen = c.BeginSession(0);

        var d = c.OnTrackTransitioned(gen, 1, @"c:\M\B.FLAC", Queue);

        Assert.True(d.ShouldAdvance);
        Assert.Equal(1, d.NextIndex);
    }

    [Fact]
    public void Duplicate_paths_in_queue_resolve_to_the_next_occurrence()
    {
        string[] queue = ["x.flac", "y.flac", "x.flac"];
        var c = Create();
        var gen = c.BeginSession(0);

        var d = c.OnTrackTransitioned(gen, 1, "x.flac", queue);

        Assert.Equal(2, d.NextIndex);
    }

    [Fact]
    public void Queue_edit_rebase_changes_where_completion_advances()
    {
        var c = Create();
        var gen = c.BeginSession(1);
        c.SetCurrentIndex(0); // an item before the playing one was removed

        var d = c.OnPlaybackCompleted(gen, queueCount: 2);

        Assert.True(d.ShouldAdvance);
        Assert.Equal(1, d.NextIndex);
    }

    [Fact]
    public void Detached_session_starts_from_the_beginning_of_the_queue()
    {
        var c = Create();
        var gen = c.BeginSession(-1);

        var d = c.OnPlaybackCompleted(gen, Queue.Length);

        Assert.True(d.ShouldAdvance);
        Assert.Equal(0, d.NextIndex);
    }

    [Fact]
    public void PeekNextIndex_reports_following_item_or_null()
    {
        var c = Create();
        c.BeginSession(1);
        Assert.Equal(2, c.PeekNextIndex(3));
        Assert.Null(c.PeekNextIndex(2));

        c.BeginSession(-1);
        Assert.Equal(0, c.PeekNextIndex(1));
        Assert.Null(c.PeekNextIndex(0));
    }

    [Fact]
    public void Generation_increases_on_every_session_change()
    {
        var c = Create();
        var g0 = c.Generation;
        var g1 = c.BeginSession(0);
        var g2 = c.Invalidate();

        Assert.True(g1 > g0);
        Assert.True(g2 > g1);
        Assert.True(c.IsCurrent(g2));
        Assert.False(c.IsCurrent(g1));
    }

    [Fact]
    public void Custom_window_controls_what_counts_as_the_same_cycle()
    {
        var c = Create(TimeSpan.FromSeconds(2));
        var gen = c.BeginSession(0);
        c.OnTrackTransitioned(gen, 1, Queue[1], Queue);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(QueueAdvanceReason.AlreadyAdvancedViaTransition, c.OnPlaybackCompleted(gen, 3).Reason);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(c.OnPlaybackCompleted(gen, 3).ShouldAdvance);
    }

    [Fact]
    public void Negative_window_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueueAdvanceCoordinator(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task Concurrent_completions_advance_at_most_once()
    {
        var c = Create();
        var gen = c.BeginSession(0);
        var advances = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (c.OnPlaybackCompleted(gen, Queue.Length).ShouldAdvance)
            {
                Interlocked.Increment(ref advances);
            }
        })));

        Assert.Equal(1, advances);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }
}
