using Sonora.Audio.Formats;
using Sonora.Audio.Playback;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Arms <see cref="WasapiAudioOutput.ArmManagedOutputCapture"/> when the player's logical
/// <see cref="AudioPlayer.Position"/> reaches a target within the active track, then collects a
/// bounded window of <see cref="PcmSampleProviderAdapter"/> output (not MixFormat engine PCM).
/// Polling stays off the WASAPI callback.
/// </summary>
internal static class ManagedOutputPositionCapture
{
    /// <summary>
    /// Waits until <paramref name="player"/> reports <paramref name="positionStart"/> (or greater)
    /// while playing, then captures <paramref name="window"/> of interleaved floats at the
    /// managed→WASAPI boundary.
    /// </summary>
    public static async Task<ManagedOutputCaptureResult> CaptureWindowAtPositionAsync(
        AudioPlayer player,
        Func<WasapiAudioOutput?> getOutput,
        TimeSpan positionStart,
        TimeSpan window,
        TimeSpan timeout,
        Action<string>? reportPhase = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(getOutput);
        ArgumentOutOfRangeException.ThrowIfLessThan(positionStart, TimeSpan.Zero);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        reportPhase?.Invoke("waitingForPosition");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        WasapiAudioOutput? output = null;
        AudioFormat format = default;

        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output = getOutput();
            if (output?.Format is { } fmt
                && player.State is PlaybackState.Playing or PlaybackState.Buffering
                && player.Position + TimeSpan.FromMilliseconds(5) >= positionStart)
            {
                format = fmt;
                break;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        if (output is null || output.Format is null)
        {
            throw new TimeoutException(
                $"Timed out waiting for WASAPI output format before position {positionStart}.");
        }

        if (player.Position + TimeSpan.FromMilliseconds(5) < positionStart)
        {
            throw new TimeoutException(
                $"Timed out waiting for Position>={positionStart} (last={player.Position}, state={player.State}).");
        }

        var captureSamples = (int)format.TimeToSamples(window);
        if (captureSamples < 1)
        {
            throw new InvalidOperationException("Capture window produced zero samples.");
        }

        // Arm with skip=0: Position already places us at the equivalent section of B.
        output.ArmManagedOutputCapture(skipSamples: 0, captureSamples: captureSamples);
        reportPhase?.Invoke("captureArmed");

        var armedAt = player.Position;
        var transitionId = player.GetDiagnostics().LastPromoteTransitionId;

        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.IsManagedOutputCaptureComplete)
            {
                break;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        if (!output.IsManagedOutputCaptureComplete)
        {
            throw new TimeoutException("Managed WASAPI-boundary capture did not complete.");
        }

        reportPhase?.Invoke("captureCompleted");

        var buffer = new float[captureSamples];
        if (!output.TryCopyManagedOutputCapture(buffer, out var written) || written <= 0)
        {
            throw new InvalidOperationException("Failed to copy managed output capture.");
        }

        if (written < buffer.Length)
        {
            buffer = buffer.AsSpan(0, written).ToArray();
        }

        var diag = player.GetDiagnostics();
        return new ManagedOutputCaptureResult(
            Samples: buffer,
            Format: format,
            ArmedAtPosition: armedAt,
            RequestedStart: positionStart,
            Window: window,
            SourcePath: player.SourcePath,
            TransitionId: transitionId,
            LastSilenceReason: diag.LastSilenceReason.ToString(),
            UnderrunCount: diag.UnderrunCount,
            OutputMode: output.Mode.ToString(),
            EndpointId: output.EndpointId);
    }
}

/// <summary>Result of a position-gated managed-boundary capture (diagnostic only).</summary>
internal sealed record ManagedOutputCaptureResult(
    float[] Samples,
    AudioFormat Format,
    TimeSpan ArmedAtPosition,
    TimeSpan RequestedStart,
    TimeSpan Window,
    string? SourcePath,
    int TransitionId,
    string LastSilenceReason,
    long UnderrunCount,
    string OutputMode,
    string? EndpointId);
