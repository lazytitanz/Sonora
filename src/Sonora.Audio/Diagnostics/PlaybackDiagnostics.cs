namespace Sonora.Audio.Diagnostics;

/// <summary>
/// Lightweight diagnostics snapshot for a player.
/// </summary>
public sealed class PlaybackDiagnostics
{
    /// <summary>Number of underrun episodes observed since the last load.</summary>
    public long UnderrunCount { get; init; }

    /// <summary>Configured ring-buffer capacity in interleaved samples.</summary>
    public int BufferCapacitySamples { get; init; }

    /// <summary>Current buffered interleaved sample count.</summary>
    public int BufferedSamples { get; init; }
}
