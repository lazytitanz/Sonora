using Sonora.TestPlayer.Services;

namespace Sonora.TestPlayer.Tests;

public sealed class VolumeUiSyncTests
{
    [Fact]
    public void Snapshot_applies_when_idle()
    {
        var sync = new VolumeUiSync();
        Assert.True(sync.AllowsSnapshotApply);
    }

    [Fact]
    public void Snapshot_blocked_while_dragging()
    {
        var sync = new VolumeUiSync();
        sync.BeginDrag();
        Assert.False(sync.AllowsSnapshotApply);
        sync.EndDrag();
        Assert.True(sync.AllowsSnapshotApply);
    }

    [Fact]
    public void Snapshot_blocked_while_command_pending()
    {
        var sync = new VolumeUiSync();
        var epoch = sync.BeginCommand(0.4f);
        Assert.False(sync.AllowsSnapshotApply);
        Assert.Equal(0.4f, sync.PendingLevel);
        Assert.True(sync.CompleteCommand(epoch, 0.4f));
        Assert.True(sync.AllowsSnapshotApply);
        Assert.Null(sync.PendingLevel);
    }

    [Fact]
    public void Stale_command_completion_does_not_clear_newer_pending()
    {
        var sync = new VolumeUiSync();
        var first = sync.BeginCommand(0.2f);
        var second = sync.BeginCommand(0.8f);
        Assert.False(sync.CompleteCommand(first, 0.2f));
        Assert.Equal(0.8f, sync.PendingLevel);
        Assert.False(sync.AllowsSnapshotApply);
        Assert.True(sync.CompleteCommand(second, 0.8f));
        Assert.True(sync.AllowsSnapshotApply);
    }

    [Fact]
    public void Failed_stale_command_does_not_clear_newer_pending()
    {
        var sync = new VolumeUiSync();
        var first = sync.BeginCommand(0.1f);
        _ = sync.BeginCommand(0.9f);
        sync.FailCommand(first);
        Assert.Equal(0.9f, sync.PendingLevel);
        Assert.False(sync.AllowsSnapshotApply);
    }

    [Fact]
    public void Drag_plus_pending_both_required_to_clear_before_snapshot()
    {
        var sync = new VolumeUiSync();
        sync.BeginDrag();
        var epoch = sync.BeginCommand(0.55f);
        sync.EndDrag();
        Assert.False(sync.AllowsSnapshotApply);
        sync.CompleteCommand(epoch, 0.55f);
        Assert.True(sync.AllowsSnapshotApply);
    }
}
