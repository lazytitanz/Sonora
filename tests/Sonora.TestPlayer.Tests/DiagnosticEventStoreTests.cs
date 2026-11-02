using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Tests;

public sealed class DiagnosticEventStoreTests
{
    [Fact]
    public void Ring_keeps_newest_events_oldest_first_when_full()
    {
        var store = new DiagnosticEventStore(capacity: 3);

        for (var i = 1; i <= 5; i++)
        {
            store.Add("playback", DiagnosticLevel.Info, "e" + i, "m");
        }

        var snapshot = store.Snapshot();
        Assert.Equal(new[] { "e3", "e4", "e5" }, snapshot.Select(e => e.Name));
        Assert.Equal(3, store.Count);
        Assert.Equal(5, store.TotalAdded);
        Assert.Equal(5, store.LastSeq);
    }

    [Fact]
    public void Sequence_numbers_and_timestamps_are_monotonic()
    {
        var store = new DiagnosticEventStore();
        var a = store.Add("c", DiagnosticLevel.Info, "a", "m");
        var b = store.Add("c", DiagnosticLevel.Info, "b", "m");

        Assert.True(b.Seq > a.Seq);
        Assert.True(b.ElapsedMs >= a.ElapsedMs);
        Assert.Equal(TimeSpan.Zero, a.UtcTime.Offset);
    }

    [Fact]
    public void Clear_empties_ring_but_keeps_sequence_and_totals()
    {
        var store = new DiagnosticEventStore();
        store.Add("c", DiagnosticLevel.Error, "boom", "m");

        store.Clear();
        var next = store.Add("c", DiagnosticLevel.Info, "after", "m");

        Assert.Equal(1, store.Count);
        Assert.Equal(2, next.Seq);
        Assert.Equal(1, store.TotalByName("boom"));
        Assert.Equal(1, store.TotalByLevel(DiagnosticLevel.Error));
    }

    [Fact]
    public void Cumulative_counters_survive_eviction()
    {
        var store = new DiagnosticEventStore(capacity: 2);
        for (var i = 0; i < 10; i++)
        {
            store.Add("playback", DiagnosticLevel.Warning, DiagnosticEventNames.Underrun, "m");
        }

        Assert.Equal(2, store.Count);
        Assert.Equal(10, store.TotalByName(DiagnosticEventNames.Underrun));
        Assert.Equal(10, store.TotalByLevel(DiagnosticLevel.Warning));
    }

    [Fact]
    public void Query_filters_by_category_level_sequence_text_and_limit()
    {
        var store = new DiagnosticEventStore();
        store.Add("playback", DiagnosticLevel.Info, "playback.loaded", "Loaded song");
        store.Add("device", DiagnosticLevel.Warning, "device.lost", "Device lost");
        store.Add("playback", DiagnosticLevel.Error, "playback.error", "Decode failed");
        store.Add("playback", DiagnosticLevel.Debug, "playback.state", "State changed");

        Assert.Equal(3, store.Query(category: "PLAYBACK").Count);
        Assert.Equal(2, store.Query(minLevel: DiagnosticLevel.Warning).Count);
        Assert.Equal(2, store.Query(afterSeq: 2).Count);
        Assert.Single(store.Query(textContains: "decode"));
        Assert.Equal(
            new[] { "playback.error", "playback.state" },
            store.Query(category: "playback", limit: 2).Select(e => e.Name));
        Assert.Equal(2, store.Query(limit: 2).Count);
    }

    [Fact]
    public void Add_with_tuples_builds_data_dictionary()
    {
        var store = new DiagnosticEventStore();

        var evt = store.Add("c", DiagnosticLevel.Info, "n", "m", ("k1", "v1"), ("k2", "v2"));

        Assert.NotNull(evt.Data);
        Assert.Equal("v1", evt.Data!["k1"]);
        Assert.Equal("v2", evt.Data["k2"]);
    }

    [Fact]
    public void EventAdded_is_raised_and_handler_exceptions_do_not_break_add()
    {
        var store = new DiagnosticEventStore();
        var invoked = 0;
        store.EventAdded += _ =>
        {
            invoked++;
            throw new InvalidOperationException("observer bug");
        };

        var evt = store.Add("c", DiagnosticLevel.Info, "n", "m");
        var second = store.Add("c", DiagnosticLevel.Info, "n2", "m");

        Assert.Equal(2, invoked);
        Assert.Equal(2, store.Count);
        Assert.Equal(1, evt.Seq);
        Assert.Equal(2, second.Seq);
    }

    [Fact]
    public async Task Concurrent_adds_keep_ring_bounded_and_sequences_unique()
    {
        var store = new DiagnosticEventStore(capacity: 100);
        const int threads = 8;
        const int perThread = 500;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                store.Add("c", DiagnosticLevel.Info, "n" + t, "m");
            }
        })));

        var snapshot = store.Snapshot();
        Assert.Equal(100, snapshot.Count);
        Assert.Equal(threads * perThread, store.TotalAdded);
        Assert.Equal(snapshot.Count, snapshot.Select(e => e.Seq).Distinct().Count());
        Assert.Equal(snapshot.OrderBy(e => e.Seq).Select(e => e.Seq), snapshot.Select(e => e.Seq));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticEventStore(0));
        var store = new DiagnosticEventStore();
        Assert.Throws<ArgumentException>(() => store.Add("", DiagnosticLevel.Info, "n", "m"));
        Assert.Throws<ArgumentException>(() => store.Add("c", DiagnosticLevel.Info, "", "m"));
    }
}
