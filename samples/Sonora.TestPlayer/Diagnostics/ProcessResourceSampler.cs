using System.Diagnostics;

namespace Sonora.TestPlayer.Diagnostics;

/// <summary>
/// A point-in-time reading of <b>process-level</b> resource usage for the whole test player process
/// (WinForms, WebView2 host, managed heap, and Sonora together). Values are not attributable to
/// Sonora alone; use trends over time, not absolute numbers, when looking for leaks.
/// </summary>
public sealed record ProcessResourceSample
{
    /// <summary>Always <c>process</c>: these numbers describe the entire process.</summary>
    public string Scope { get; init; } = "process";

    /// <summary>UTC time of the reading.</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Process working set in bytes.</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>Process private bytes (committed private memory).</summary>
    public long PrivateBytes { get; init; }

    /// <summary>Managed heap size from <see cref="GC.GetTotalMemory(bool)"/> (no forced collection).</summary>
    public long GcTotalMemoryBytes { get; init; }

    /// <summary>Gen 0 collection count since process start.</summary>
    public int Gen0Collections { get; init; }

    /// <summary>Gen 1 collection count since process start.</summary>
    public int Gen1Collections { get; init; }

    /// <summary>Gen 2 collection count since process start.</summary>
    public int Gen2Collections { get; init; }

    /// <summary>Open OS handle count for the process.</summary>
    public int HandleCount { get; init; }

    /// <summary>Number of OS threads in the process.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Seconds since this sampler was created (application uptime).</summary>
    public double UptimeSeconds { get; init; }
}

/// <summary>
/// Samples process-level resource counters. Cheap enough to call every second or two from a UI timer.
/// Thread-safe.
/// </summary>
public sealed class ProcessResourceSampler
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private ProcessResourceSample? _latest;

    /// <summary>The most recent sample taken by <see cref="Sample"/>, or <see langword="null"/>.</summary>
    public ProcessResourceSample? Latest => Volatile.Read(ref _latest);

    /// <summary>Takes a new sample and stores it as <see cref="Latest"/>.</summary>
    public ProcessResourceSample Sample()
    {
        long workingSet = 0;
        long privateBytes = 0;
        var handles = 0;
        var threads = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
            privateBytes = process.PrivateMemorySize64;
            handles = process.HandleCount;
            threads = process.Threads.Count;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Counters are best-effort diagnostics; leave zeros rather than failing the caller.
            Debug.WriteLine($"[ProcessResourceSampler] Process counters unavailable: {ex.Message}");
        }

        var sample = new ProcessResourceSample
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            WorkingSetBytes = workingSet,
            PrivateBytes = privateBytes,
            GcTotalMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            HandleCount = handles,
            ThreadCount = threads,
            UptimeSeconds = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds,
        };
        Volatile.Write(ref _latest, sample);
        return sample;
    }
}
