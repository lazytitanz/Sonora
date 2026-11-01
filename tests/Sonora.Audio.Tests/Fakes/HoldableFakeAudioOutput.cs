namespace Sonora.Audio.Tests.Fakes;

/// <summary>
/// Fake output whose pull loop and/or <see cref="FakeAudioOutput.Shutdown"/> can be blocked for concurrency tests.
/// </summary>
internal sealed class HoldableFakeAudioOutput : FakeAudioOutput
{
    private readonly ManualResetEventSlim _allowRead = new(true);
    private readonly ManualResetEventSlim _enteredReadGate = new(false);
    private readonly ManualResetEventSlim _allowShutdown = new(true);
    private readonly ManualResetEventSlim _enteredShutdownGate = new(false);
    private readonly ManualResetEventSlim _shutdownSignaled = new(false);
    private int _holdRead;
    private int _holdShutdown;

    public HoldableFakeAudioOutput(string? endpointId = null, TimeSpan? shutdownTimeout = null)
        : base(
            blockSamples: 64,
            latency: TimeSpan.FromMilliseconds(5),
            endpointId: endpointId,
            shutdownTimeout: shutdownTimeout ?? TimeSpan.FromSeconds(10))
    {
    }

    /// <summary>
    /// When <see langword="true"/> (default), <see cref="OnShutdownSignaled"/> unblocks a held read so Join can succeed.
    /// Set to <see langword="false"/> to simulate a stuck consumer that prevents quiescence.
    /// </summary>
    public bool ReleaseReadOnShutdown { get; set; } = true;

    /// <summary>Blocks subsequent pull-loop iterations until <see cref="ReleaseRead"/> or shutdown (when allowed).</summary>
    public void HoldRead()
    {
        _enteredReadGate.Reset();
        Interlocked.Exchange(ref _holdRead, 1);
        _allowRead.Reset();
    }

    /// <summary>Allows a blocked pull loop to proceed.</summary>
    public void ReleaseRead()
    {
        Interlocked.Exchange(ref _holdRead, 0);
        _allowRead.Set();
    }

    /// <summary>
    /// Blocks <see cref="FakeAudioOutput.Shutdown"/> after the pull loop has been signaled to exit,
    /// until <see cref="ReleaseShutdown"/>. Use this to observe the Switching / detached window.
    /// </summary>
    public void HoldShutdown()
    {
        _enteredShutdownGate.Reset();
        Interlocked.Exchange(ref _holdShutdown, 1);
        _allowShutdown.Reset();
    }

    /// <summary>Allows a blocked <see cref="FakeAudioOutput.Shutdown"/> to proceed to join.</summary>
    public void ReleaseShutdown()
    {
        Interlocked.Exchange(ref _holdShutdown, 0);
        _allowShutdown.Set();
    }

    /// <summary>Waits until the pull loop is blocked in <see cref="BeforeRead"/>.</summary>
    public bool WaitUntilReadHeld(TimeSpan timeout) => _enteredReadGate.Wait(timeout);

    /// <summary>Waits until <see cref="Shutdown"/> is blocked in <see cref="OnShutdownSignaled"/>.</summary>
    public bool WaitUntilShutdownHeld(TimeSpan timeout) => _enteredShutdownGate.Wait(timeout);

    protected override void BeforeRead()
    {
        if (Volatile.Read(ref _holdRead) == 0)
        {
            return;
        }

        _enteredReadGate.Set();

        while (Volatile.Read(ref _holdRead) == 1)
        {
            if (ReleaseReadOnShutdown && _shutdownSignaled.IsSet)
            {
                break;
            }

            if (_allowRead.Wait(50))
            {
                if (Volatile.Read(ref _holdRead) == 0)
                {
                    break;
                }

                if (ReleaseReadOnShutdown && _shutdownSignaled.IsSet)
                {
                    break;
                }

                _allowRead.Reset();
            }
        }
    }

    protected override void OnShutdownSignaled()
    {
        _shutdownSignaled.Set();
        if (ReleaseReadOnShutdown)
        {
            ReleaseRead();
        }

        if (Volatile.Read(ref _holdShutdown) == 0)
        {
            return;
        }

        _enteredShutdownGate.Set();
        _allowShutdown.Wait();
    }
}
