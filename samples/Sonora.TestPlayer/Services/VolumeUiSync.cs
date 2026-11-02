namespace Sonora.TestPlayer.Services;

/// <summary>
/// Pure gate used by tests (and mirrored in the web UI) to decide whether an engine volume
/// snapshot may overwrite the slider. While the user is dragging, or a setVolume command has not
/// yet reconciled into the shared snapshot, engine snapshots must not move the thumb.
/// </summary>
public sealed class VolumeUiSync
{
    private bool _dragging;
    private float? _pending;
    private int _epoch;

    /// <summary>True while a pointer drag is active on the volume control.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Level of the newest unsettled setVolume request, if any.</summary>
    public float? PendingLevel => _pending;

    /// <summary>Whether a snapshot's volume field may be written into the slider.</summary>
    public bool AllowsSnapshotApply => !_dragging && _pending is null;

    /// <summary>Marks the start of a pointer drag.</summary>
    public void BeginDrag() => _dragging = true;

    /// <summary>Marks the end of a pointer drag.</summary>
    public void EndDrag() => _dragging = false;

    /// <summary>
    /// Records a newly requested level (optimistic). Returns an epoch that must be presented to
    /// <see cref="CompleteCommand"/> so a stale response cannot clear a newer pending value.
    /// </summary>
    public int BeginCommand(float level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 1f);
        _pending = level;
        return ++_epoch;
    }

    /// <summary>
    /// Clears the pending level when <paramref name="epoch"/> is still current and returns whether
    /// this completion is authoritative for the UI.
    /// </summary>
    public bool CompleteCommand(int epoch, float appliedLevel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(appliedLevel, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(appliedLevel, 1f);
        if (epoch != _epoch)
        {
            return false;
        }

        _pending = null;
        return true;
    }

    /// <summary>Clears pending state after a failed command so the UI can resync from the engine.</summary>
    public void FailCommand(int epoch)
    {
        if (epoch == _epoch)
        {
            _pending = null;
        }
    }
}
