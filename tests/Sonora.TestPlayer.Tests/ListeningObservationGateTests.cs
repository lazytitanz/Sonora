using Sonora.TestPlayer.Diagnostics;

namespace Sonora.TestPlayer.Tests;

public sealed class ListeningObservationGateTests
{
    [Fact]
    public async Task Submit_Associates_Observation_With_AttemptId()
    {
        var gate = new ListeningObservationGate();
        var wait = gate.WaitAsync("attempt-1", "How did it sound?");
        Assert.Equal("attempt-1", gate.PendingAttemptId);

        Assert.True(gate.TrySubmit("attempt-1", ListeningClassification.Static, notes: "clear static"));
        var obs = await wait;
        Assert.Equal("attempt-1", obs.AttemptId);
        Assert.Equal(ListeningClassification.Static, obs.Classification);
        Assert.Equal("clear static", obs.Notes);
        Assert.Null(gate.PendingAttemptId);
    }

    [Fact]
    public void Submit_WrongAttemptId_IsRejected()
    {
        var gate = new ListeningObservationGate();
        _ = gate.WaitAsync("attempt-a", "prompt");
        Assert.False(gate.TrySubmit("attempt-b", ListeningClassification.Clean));
        Assert.Equal("attempt-a", gate.PendingAttemptId);
    }

    [Fact]
    public async Task Cancel_Pending_Cancels_Wait()
    {
        var gate = new ListeningObservationGate();
        var wait = gate.WaitAsync("attempt-x", "prompt");
        gate.CancelPending();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
        Assert.Null(gate.PendingAttemptId);
    }
}
