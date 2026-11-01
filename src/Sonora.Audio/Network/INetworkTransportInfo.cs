namespace Sonora.Audio.Network;

/// <summary>
/// Optional capability implemented by network-backed decoders so <see cref="AudioPlayer"/>
/// can expose transport status without coupling to HTTP types in the decode pipeline.
/// </summary>
public interface INetworkTransportInfo
{
    /// <summary>Current network transport status.</summary>
    NetworkTransportStatus TransportStatus { get; }

    /// <summary>Bytes currently held in the bounded network buffer.</summary>
    int NetworkBytesBuffered { get; }

    /// <summary>Raised when <see cref="TransportStatus"/> or buffered bytes change meaningfully.</summary>
    event EventHandler? TransportChanged;
}
