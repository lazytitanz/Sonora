using System.Diagnostics;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Diagnostics;

/// <summary>Well-known diagnostic event names shared by controllers and scenario tracking.</summary>
public static class DiagnosticEventNames
{
    /// <summary>The engine raised <c>ErrorOccurred</c>.</summary>
    public const string PlaybackError = "playback.error";

    /// <summary>The engine raised <c>Underrun</c>.</summary>
    public const string Underrun = "playback.underrun";

    /// <summary>The engine raised <c>PlaybackCompleted</c>.</summary>
    public const string Completed = "playback.completed";

    /// <summary>The engine raised <c>TrackTransitioned</c>.</summary>
    public const string Transitioned = "playback.transitioned";
}

/// <summary>
/// Bounded, thread-safe ring of <see cref="DiagnosticEventDto"/> values. When full, the oldest event is
/// overwritten. Cumulative counters survive both eviction and <see cref="Clear"/>-independent reads, so
/// long soak sessions can count events beyond the ring capacity.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safety: all members are safe to call concurrently. The internal lock is held only for the O(1) ring
/// update; <see cref="EventAdded"/> is raised after the lock is released and handler exceptions are contained.
/// </para>
/// <para>Do not call <see cref="Add"/> from a real-time audio callback; callers marshal engine events first.</para>
/// </remarks>
public sealed class DiagnosticEventStore
{
    /// <summary>Default ring capacity.</summary>
    public const int DefaultCapacity = 5000;

    private readonly object _sync = new();
    private readonly DiagnosticEventDto?[] _ring;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly long[] _levelTotals = new long[Enum.GetValues<DiagnosticLevel>().Length];
    private readonly Dictionary<string, long> _nameTotals = new(StringComparer.Ordinal);
    private int _head;
    private int _count;
    private long _lastSeq;
    private long _totalAdded;

    /// <summary>Creates a store.</summary>
    /// <param name="capacity">Maximum retained events (at least 1).</param>
    public DiagnosticEventStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new DiagnosticEventDto?[capacity];
    }

    /// <summary>Raised (outside the internal lock) after an event is stored. May run on any thread.</summary>
    public event Action<DiagnosticEventDto>? EventAdded;

    /// <summary>Ring capacity.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Number of currently retained events.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Sequence number of the most recent event, or 0 when none was ever added.</summary>
    public long LastSeq => Interlocked.Read(ref _lastSeq);

    /// <summary>Total events ever added (not reduced by eviction or <see cref="Clear"/>).</summary>
    public long TotalAdded => Interlocked.Read(ref _totalAdded);

    /// <summary>Records an event.</summary>
    /// <returns>The stored event.</returns>
    public DiagnosticEventDto Add(
        string category,
        DiagnosticLevel level,
        string name,
        string message,
        IReadOnlyDictionary<string, string>? data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(message);

        DiagnosticEventDto evt;
        lock (_sync)
        {
            var seq = ++_lastSeq;
            var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            evt = new DiagnosticEventDto(seq, elapsedMs, DateTimeOffset.UtcNow, category, level, name, message, data);

            var index = (_head + _count) % _ring.Length;
            if (_count == _ring.Length)
            {
                _ring[_head] = evt;
                _head = (_head + 1) % _ring.Length;
            }
            else
            {
                _ring[index] = evt;
                _count++;
            }

            _totalAdded++;
            _levelTotals[(int)level]++;
            _nameTotals[name] = _nameTotals.GetValueOrDefault(name) + 1;
        }

        RaiseEventAdded(evt);
        return evt;
    }

    /// <summary>Convenience overload that builds the data dictionary from key/value pairs.</summary>
    public DiagnosticEventDto Add(
        string category,
        DiagnosticLevel level,
        string name,
        string message,
        params (string Key, string Value)[] data)
    {
        Dictionary<string, string>? dict = null;
        if (data.Length > 0)
        {
            dict = new Dictionary<string, string>(data.Length, StringComparer.Ordinal);
            foreach (var (key, value) in data)
            {
                dict[key] = value;
            }
        }

        return Add(category, level, name, message, dict);
    }

    /// <summary>Removes all retained events. Sequence numbers and cumulative counters are not reset.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Returns retained events oldest-first.</summary>
    public IReadOnlyList<DiagnosticEventDto> Snapshot() => Query();

    /// <summary>Returns retained events oldest-first, optionally filtered.</summary>
    /// <param name="category">Exact category (case-insensitive), or <see langword="null"/> for all.</param>
    /// <param name="minLevel">Minimum severity, or <see langword="null"/> for all.</param>
    /// <param name="afterSeq">Only events with a greater sequence number.</param>
    /// <param name="textContains">Case-insensitive substring match on name or message.</param>
    /// <param name="limit">When set, keeps only the newest <paramref name="limit"/> matches.</param>
    public IReadOnlyList<DiagnosticEventDto> Query(
        string? category = null,
        DiagnosticLevel? minLevel = null,
        long? afterSeq = null,
        string? textContains = null,
        int? limit = null)
    {
        var result = new List<DiagnosticEventDto>();
        lock (_sync)
        {
            for (var i = 0; i < _count; i++)
            {
                var evt = _ring[(_head + i) % _ring.Length]!;
                if (afterSeq is { } after && evt.Seq <= after)
                {
                    continue;
                }

                if (minLevel is { } min && evt.Level < min)
                {
                    continue;
                }

                if (category is not null && !string.Equals(evt.Category, category, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(textContains)
                    && !evt.Name.Contains(textContains, StringComparison.OrdinalIgnoreCase)
                    && !evt.Message.Contains(textContains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(evt);
            }
        }

        if (limit is { } max && max >= 0 && result.Count > max)
        {
            result.RemoveRange(0, result.Count - max);
        }

        return result;
    }

    /// <summary>Cumulative number of events ever added at the given level (survives eviction).</summary>
    public long TotalByLevel(DiagnosticLevel level)
    {
        lock (_sync)
        {
            return _levelTotals[(int)level];
        }
    }

    /// <summary>Cumulative number of events ever added with the given name (survives eviction).</summary>
    public long TotalByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            return _nameTotals.GetValueOrDefault(name);
        }
    }

    private void RaiseEventAdded(DiagnosticEventDto evt)
    {
        var handler = EventAdded;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(evt);
        }
        catch (Exception ex)
        {
            // A faulty observer must never break the code path that recorded the event.
            Debug.WriteLine($"[DiagnosticEventStore] EventAdded handler failed: {ex}");
        }
    }
}
