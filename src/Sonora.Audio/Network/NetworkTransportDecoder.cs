using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Seeking;

namespace Sonora.Audio.Network;

/// <summary>
/// Wraps a decoder opened over an HTTP byte stream to expose transport status.
/// Disposing this type disposes the inner decoder (which owns the HTTP stream).
/// </summary>
internal sealed class NetworkTransportDecoder : IAudioDecoder, INetworkTransportInfo
{
    private readonly IAudioDecoder _inner;
    private readonly Func<NetworkTransportStatus> _getStatus;
    private readonly Func<int> _getBuffered;
    private readonly Action<EventHandler> _subscribe;
    private readonly Action<EventHandler> _unsubscribe;
    private readonly EventHandler _handler;
    private bool _disposed;

    public NetworkTransportDecoder(
        IAudioDecoder inner,
        Func<NetworkTransportStatus> getStatus,
        Func<int> getBuffered,
        Action<EventHandler> subscribe,
        Action<EventHandler> unsubscribe)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
        _getBuffered = getBuffered ?? throw new ArgumentNullException(nameof(getBuffered));
        _subscribe = subscribe ?? throw new ArgumentNullException(nameof(subscribe));
        _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
        _handler = OnTransportChanged;
        _subscribe(_handler);
    }

    public static NetworkTransportDecoder ForHttpContent(HttpContentByteStream transport, IAudioDecoder inner)
        => new(
            inner,
            () => transport.Status,
            () => transport.BufferedBytes,
            h => transport.StatusChanged += h,
            h => transport.StatusChanged -= h);

    public static NetworkTransportDecoder ForRandomAccess(HttpRandomAccessStream transport, IAudioDecoder inner)
        => new(
            inner,
            () => transport.Status,
            () => transport.BufferedBytes,
            h => transport.StatusChanged += h,
            h => transport.StatusChanged -= h);

    /// <summary>
    /// Wraps a decoder whose bytes are fetched outside Sonora's HTTP transport (for example MF URL open).
    /// </summary>
    public static NetworkTransportDecoder ForExternalReady(IAudioDecoder inner)
        => new(
            inner,
            () => NetworkTransportStatus.Ready,
            () => 0,
            _ => { },
            _ => { });

    public AudioFormat Format => _inner.Format;
    public TimeSpan Duration => _inner.Duration;
    public bool HasKnownDuration => _inner.HasKnownDuration;
    public bool CanSeek => _inner.CanSeek;

    public NetworkTransportStatus TransportStatus => _getStatus();
    public int NetworkBytesBuffered => _getBuffered();

    public event EventHandler? TransportChanged;

    public int Read(Span<float> destination) => _inner.Read(destination);

    public AudioSeekResult Seek(TimeSpan position) => _inner.Seek(position);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _unsubscribe(_handler);
        }
        catch
        {
            // ignored
        }

        _inner.Dispose();
    }

    private void OnTransportChanged(object? sender, EventArgs e)
    {
        try
        {
            TransportChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // ignored
        }
    }
}
