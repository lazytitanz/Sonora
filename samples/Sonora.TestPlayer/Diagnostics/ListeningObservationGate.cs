namespace Sonora.TestPlayer.Diagnostics;

/// <summary>
/// Bridges automated listening scenarios to a UI observation (Clean / Static / Unsure).
/// Waits are cancellable; submissions must match the pending attempt id.
/// </summary>
public sealed class ListeningObservationGate
{
    private readonly object _gate = new();
    private string? _pendingAttemptId;
    private string? _pendingPrompt;
    private TaskCompletionSource<ListeningObservation>? _tcs;

    public string? PendingAttemptId
    {
        get { lock (_gate) return _pendingAttemptId; }
    }

    public string? PendingPrompt
    {
        get { lock (_gate) return _pendingPrompt; }
    }

    /// <summary>
    /// Arms a wait for <paramref name="attemptId"/>. Cancels any previous pending wait as superseded.
    /// </summary>
    public Task<ListeningObservation> WaitAsync(
        string attemptId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        TaskCompletionSource<ListeningObservation> tcs;
        lock (_gate)
        {
            _tcs?.TrySetCanceled();
            _pendingAttemptId = attemptId;
            _pendingPrompt = prompt;
            tcs = new TaskCompletionSource<ListeningObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tcs = tcs;
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                lock (_gate)
                {
                    if (_tcs == tcs)
                    {
                        _pendingAttemptId = null;
                        _pendingPrompt = null;
                        _tcs = null;
                    }
                }

                tcs.TrySetCanceled(cancellationToken);
            });
        }

        return tcs.Task;
    }

    /// <summary>
    /// Completes the pending wait when <paramref name="attemptId"/> matches.
    /// </summary>
    /// <returns><see langword="true"/> when the observation was accepted.</returns>
    public bool TrySubmit(string attemptId, ListeningClassification classification, string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        TaskCompletionSource<ListeningObservation>? tcs;
        lock (_gate)
        {
            if (!string.Equals(_pendingAttemptId, attemptId, StringComparison.Ordinal))
            {
                return false;
            }

            tcs = _tcs;
            _pendingAttemptId = null;
            _pendingPrompt = null;
            _tcs = null;
        }

        if (tcs is null)
        {
            return false;
        }

        return tcs.TrySetResult(new ListeningObservation(attemptId, classification, notes, DateTimeOffset.UtcNow));
    }

    public void CancelPending()
    {
        TaskCompletionSource<ListeningObservation>? tcs;
        lock (_gate)
        {
            tcs = _tcs;
            _pendingAttemptId = null;
            _pendingPrompt = null;
            _tcs = null;
        }

        tcs?.TrySetCanceled();
    }
}

public enum ListeningClassification
{
    Clean = 0,
    Static = 1,
    Unsure = 2,
}

public sealed record ListeningObservation(
    string AttemptId,
    ListeningClassification Classification,
    string? Notes,
    DateTimeOffset SubmittedAtUtc);
