namespace Sonora.Audio.Network;

/// <summary>
/// Network/source transport status, independent of <see cref="Formats.PlaybackState"/>.
/// Playback intent (Playing/Paused) is preserved while the transport buffers or reconnects.
/// </summary>
public enum NetworkTransportStatus
{
    /// <summary>No network source is active.</summary>
    Idle = 0,

    /// <summary>Opening the connection / waiting for response headers.</summary>
    Connecting = 1,

    /// <summary>Waiting for enough network bytes or PCM before steady playback.</summary>
    Buffering = 2,

    /// <summary>Transport has sufficient data for the decoder.</summary>
    Ready = 3,

    /// <summary>Recovering from a recoverable interruption.</summary>
    Reconnecting = 4,

    /// <summary>The HTTP body completed successfully.</summary>
    Ended = 5,

    /// <summary>Unrecoverable transport failure.</summary>
    Failed = 6,
}
