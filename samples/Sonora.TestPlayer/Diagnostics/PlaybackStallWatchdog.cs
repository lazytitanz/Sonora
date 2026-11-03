using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sonora.Audio;
using Sonora.Audio.Devices;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.Formats;
using Sonora.Audio.Network;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Diagnostics;

/// <summary>
/// Detects indefinite Playing+silent stalls using atomic progress counters only.
/// Never waits on the player command gate, decoder seek gate, or pipeline lock.
/// On stall, preserves the seek lifecycle trace even when a fuller snapshot fails.
/// </summary>
public sealed class PlaybackStallWatchdog
{
    public const int StallClassNone = 0;
    public const int StallClassNoDecode = 1;
    public const int StallClassDecodeNoRing = 2;
    public const int StallClassNotConsumed = 3;
    public const int StallClassNotRendered = 4;
    public const int StallClassUiStale = 5;

    private readonly DiagnosticEventStore _store;
    private readonly Func<AudioPlayer?> _getPlayer;
    private readonly Func<EngineSnapshotDto> _getSnapshot;
    private readonly Action<string, object?>? _raiseUi;

    private long _lastSamplesConsumed;
    private long _lastSamplesDecoded;
    private long _lastCallbackCount;
    private long _lastCallbackTimestamp;
    private int _quietTicks;
    private int _lastDumpClass;
    private DateTimeOffset _lastDumpUtc = DateTimeOffset.MinValue;

    /// <summary>Quiet Playing intervals before a stall is declared (each tick ~ position timer).</summary>
    public int QuietTicksRequired { get; set; } = 5;

    /// <summary>Minimum spacing between freeze dumps.</summary>
    public TimeSpan DumpCooldownTime { get; set; } = TimeSpan.FromSeconds(8);

    public PlaybackStallWatchdog(
        DiagnosticEventStore store,
        Func<AudioPlayer?> getPlayer,
        Func<EngineSnapshotDto> getSnapshot,
        Action<string, object?>? raiseUi = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _getPlayer = getPlayer ?? throw new ArgumentNullException(nameof(getPlayer));
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _raiseUi = raiseUi;
    }

    /// <summary>
    /// Called from the UI position timer. Must not block on hung player locks.
    /// </summary>
    public void Tick()
    {
        AudioPlayer? player;
        try
        {
            player = _getPlayer();
        }
        catch
        {
            return;
        }

        if (player is null)
        {
            ResetProgress();
            return;
        }

        PlaybackDiagnostics diag;
        try
        {
            diag = player.GetDiagnostics();
        }
        catch
        {
            return;
        }

        if (diag.State != PlaybackState.Playing)
        {
            ResetProgress();
            return;
        }

        // Legitimate silence windows — do not escalate.
        if (diag.SeekInProgress > 0
            || diag.EffectsBarrier != 0
            || diag.OutputDeviceStatus == OutputDeviceStatus.Switching
            || diag.LastSilenceReason is SeekSilenceReason.EffectsBarrier
                or SeekSilenceReason.OutputSwitching
                or SeekSilenceReason.Seeking
                or SeekSilenceReason.Buffering
            || diag.NetworkStatus is NetworkTransportStatus.Buffering
                or NetworkTransportStatus.Reconnecting)
        {
            _quietTicks = 0;
            CaptureProgress(diag);
            return;
        }

        var consumedDelta = diag.SamplesConsumed - _lastSamplesConsumed;
        var decodedDelta = diag.SamplesDecoded - _lastSamplesDecoded;
        var callbackDelta = diag.CallbackCount - _lastCallbackCount;
        var callbackMoved = diag.LastCallbackTimestamp != _lastCallbackTimestamp;

        CaptureProgress(diag);

        var progressing = consumedDelta > 0 || (decodedDelta > 0 && diag.BufferedSamples > 0 && callbackDelta > 0);
        if (progressing)
        {
            _quietTicks = 0;
            return;
        }

        // Callbacks still firing but only silence — still counts toward stall once quiet long enough.
        if (!callbackMoved && callbackDelta == 0 && diag.LastSilenceReason == SeekSilenceReason.None)
        {
            // No callback activity at all while Playing — strong stall signal.
            _quietTicks++;
        }
        else if (diag.LastSilenceReason is SeekSilenceReason.Underrun or SeekSilenceReason.PendingNext
                 or SeekSilenceReason.EndOfStream
                 || (callbackDelta > 0 && consumedDelta <= 0))
        {
            _quietTicks++;
        }
        else
        {
            _quietTicks = 0;
            return;
        }

        if (_quietTicks < QuietTicksRequired)
        {
            return;
        }

        var stallClass = Classify(diag, consumedDelta, decodedDelta, callbackDelta);
        if (stallClass == StallClassNone)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (stallClass == _lastDumpClass && now - _lastDumpUtc < DumpCooldownTime)
        {
            return;
        }

        _lastDumpClass = stallClass;
        _lastDumpUtc = now;
        _quietTicks = 0;
        CaptureFreezeDump(player, diag, stallClass);
    }

    private static int Classify(PlaybackDiagnostics diag, long consumedDelta, long decodedDelta, long callbackDelta)
    {
        if (diag.PumpRunning == false && decodedDelta <= 0 && diag.SamplesDecoded == 0)
        {
            return StallClassNoDecode;
        }

        if (decodedDelta <= 0 && !diag.PumpRunning)
        {
            return StallClassNoDecode;
        }

        if (decodedDelta > 0 && diag.BufferedSamples == 0 && consumedDelta <= 0)
        {
            return StallClassDecodeNoRing;
        }

        if (diag.BufferedSamples > 0 && consumedDelta <= 0)
        {
            return StallClassNotConsumed;
        }

        if (consumedDelta > 0 && callbackDelta <= 0)
        {
            return StallClassNotRendered;
        }

        if (callbackDelta > 0 && consumedDelta <= 0)
        {
            return StallClassNotConsumed;
        }

        return StallClassNotConsumed;
    }

    private void CaptureFreezeDump(AudioPlayer player, PlaybackDiagnostics diag, int stallClass)
    {
        var sb = new StringBuilder(2048);
        sb.Append("class=").Append(stallClass);
        sb.Append(" state=").Append(diag.State);
        sb.Append(" silence=").Append(diag.LastSilenceReason);
        sb.Append(" barrier=").Append(diag.EffectsBarrier);
        sb.Append(" seekInFlight=").Append(diag.SeekInProgress);
        sb.Append(" pump=").Append(diag.PumpRunning);
        sb.Append(" ringCompleted=").Append(diag.RingCompleted);
        sb.Append(" ring=").Append(diag.BufferedSamples).Append('/').Append(diag.BufferCapacitySamples);
        sb.Append(" decoded=").Append(diag.SamplesDecoded);
        sb.Append(" consumed=").Append(diag.SamplesConsumed);
        sb.Append(" callbacks=").Append(diag.CallbackCount);
        sb.Append(" lastSeek=").Append(diag.LastSeekId);
        sb.Append(" lastTerminal=").Append(diag.LastSeekTerminalId);
        sb.Append(" promote=").Append(diag.LastPromoteTransitionId);
        sb.Append(" stopPumpTimeouts=").Append(diag.StopPumpTimeoutCount);
        sb.Append(" orphanWorker=").Append(diag.OrphanDecodeWorker);
        sb.Append(" next=").Append(diag.NextTrackState);
        sb.Append(" outputStatus=").Append(diag.OutputDeviceStatus);
        sb.Append(" pipeline=").Append(diag.ActivePipelineId);
        sb.Append(" path=").Append(diag.SourcePath ?? "");

        _store.Add(
            "playback",
            DiagnosticLevel.Error,
            "playback.stall",
            "Playing-but-stalled dump: " + sb,
            ("stallClass", stallClass.ToString()),
            ("silenceReason", diag.LastSilenceReason.ToString()),
            ("lastSeekId", diag.LastSeekId.ToString()),
            ("lastSeekTerminalId", diag.LastSeekTerminalId.ToString()),
            ("stopPumpTimeoutCount", diag.StopPumpTimeoutCount.ToString()),
            ("orphanDecodeWorker", diag.OrphanDecodeWorker.ToString()),
            ("traceDropped", diag.TraceDroppedCount.ToString()),
            ("traceOverwritten", diag.TraceOverwrittenCount.ToString()));

        // Always preserve the recent seek trace — even if further snapshot work fails.
        var trace = new SeekLifecycleEvent[512];
        int copied;
        long dropped;
        long overwritten;
        try
        {
            copied = player.CopySeekLifecycleTrace(trace, out dropped, out overwritten);
        }
        catch
        {
            copied = 0;
            dropped = diag.TraceDroppedCount;
            overwritten = diag.TraceOverwrittenCount;
        }

        if (copied > 0)
        {
            try
            {
                var slice = trace.AsSpan(0, copied).ToArray();
                var json = JsonSerializer.Serialize(slice, new JsonSerializerOptions { WriteIndented = false });
                _store.Add(
                    "playback",
                    DiagnosticLevel.Error,
                    "playback.stall.trace",
                    $"Seek lifecycle trace ({copied} events, dropped={dropped}, overwritten={overwritten}).",
                    ("count", copied.ToString()),
                    ("dropped", dropped.ToString()),
                    ("overwritten", overwritten.ToString()),
                    ("eventsJson", json.Length > 12000 ? json[..12000] + "…" : json));
            }
            catch (Exception ex)
            {
                _store.Add(
                    "playback",
                    DiagnosticLevel.Warning,
                    "playback.stall.traceFailed",
                    "Failed to serialize seek trace: " + ex.Message);
            }
        }
        else
        {
            _store.Add(
                "playback",
                DiagnosticLevel.Warning,
                "playback.stall.traceEmpty",
                $"No seek trace events retained (dropped={dropped}, overwritten={overwritten}).");
        }

        // Best-effort snapshot — must not block the hang.
        try
        {
            var snap = _getSnapshot();
            _store.Add(
                "playback",
                DiagnosticLevel.Error,
                "playback.stall.snapshot",
                $"Snapshot state={snap.State} pos={snap.PositionMs:F0} next={snap.NextTrackState} seekInProgress={snap.SeekInProgress}.",
                ("positionMs", snap.PositionMs.ToString("F1")),
                ("sourcePath", snap.SourcePath ?? ""),
                ("currentItemId", snap.CurrentItemId ?? ""));
        }
        catch (Exception ex)
        {
            _store.Add(
                "playback",
                DiagnosticLevel.Warning,
                "playback.stall.snapshotFailed",
                "Best-effort snapshot failed (locks may be held): " + ex.Message);
        }

        try
        {
            var path = WriteDumpFile(stallClass, sb.ToString(), trace, copied);
            _store.Add(
                "playback",
                DiagnosticLevel.Info,
                "playback.stall.dumpFile",
                "Wrote stall dump file.",
                ("path", path));
            _raiseUi?.Invoke(
                "diagnostics.stall",
                new { stallClass, message = $"Playback stalled (class {stallClass}). Dump captured.", path });
        }
        catch (Exception ex)
        {
            _store.Add(
                "playback",
                DiagnosticLevel.Warning,
                "playback.stall.dumpFileFailed",
                "Could not write stall dump file: " + ex.Message);
            _raiseUi?.Invoke(
                "diagnostics.stall",
                new { stallClass, message = $"Playback stalled (class {stallClass}). Trace logged.", path = (string?)null });
        }
    }

    private static string WriteDumpFile(int stallClass, string summary, SeekLifecycleEvent[] trace, int copied)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sonora",
            "SonoraTestPlayer",
            "stall-dumps");
        Directory.CreateDirectory(dir);
        var name = $"stall-{DateTime.UtcNow:yyyyMMdd-HHmmss}-class{stallClass}.txt";
        var path = Path.Combine(dir, name);
        var sb = new StringBuilder();
        sb.AppendLine(summary);
        sb.AppendLine($"stopwatchFreq={Stopwatch.Frequency}");
        sb.AppendLine($"traceEvents={copied}");
        for (var i = 0; i < copied; i++)
        {
            var e = trace[i];
            sb.Append(e.Sequence).Append('\t')
                .Append(e.Timestamp).Append('\t')
                .Append(e.Stage).Append('\t')
                .Append("seek=").Append(e.SeekId).Append('\t')
                .Append("pipe=").Append(e.PipelineId).Append('\t')
                .Append("gen=").Append(e.TrackGeneration).Append('\t')
                .Append("silence=").Append(e.SilenceReason).Append('\t')
                .Append("flags=").Append(e.Flags).Append('\t')
                .Append("ring=").Append(e.RingCount).Append('\t')
                .Append("dec=").Append(e.SamplesDecoded).Append('\t')
                .Append("cons=").Append(e.SamplesConsumed).Append('\t')
                .Append("outcome=").Append(e.Outcome)
                .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private void CaptureProgress(PlaybackDiagnostics diag)
    {
        _lastSamplesConsumed = diag.SamplesConsumed;
        _lastSamplesDecoded = diag.SamplesDecoded;
        _lastCallbackCount = diag.CallbackCount;
        _lastCallbackTimestamp = diag.LastCallbackTimestamp;
    }

    private void ResetProgress()
    {
        _quietTicks = 0;
        _lastSamplesConsumed = 0;
        _lastSamplesDecoded = 0;
        _lastCallbackCount = 0;
        _lastCallbackTimestamp = 0;
    }
}
