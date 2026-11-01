namespace Sonora.Audio.Network;

/// <summary>
/// Immutable HTTP streaming configuration. Ordinary playback needs no customization.
/// </summary>
public sealed class HttpStreamingOptions
{
    /// <summary>Default options with conservative buffer and timeout limits.</summary>
    public static HttpStreamingOptions Default { get; } = new();

    /// <summary>Bytes to accumulate before considering the initial network buffer ready. Default 64 KiB.</summary>
    public int InitialBufferBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum in-memory network byte buffer. Default 1 MiB; hard-clamped to 16 MiB.</summary>
    public int MaxNetworkBufferBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// When buffered network bytes fall below this threshold during playback, the transport
    /// reports <see cref="NetworkTransportStatus.Buffering"/>. Default 32 KiB.
    /// </summary>
    public int RebufferThresholdBytes { get; init; } = 32 * 1024;

    /// <summary>Timeout for establishing a connection and receiving response headers. Default 15s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum idle time between successful body reads before treating the connection as stalled. Default 20s.</summary>
    public TimeSpan ReadIdleTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Maximum HTTP redirects to follow. Default 5.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Maximum reconnect attempts after a recoverable interruption. Default 3.</summary>
    public int MaxReconnectAttempts { get; init; } = 3;

    /// <summary>Base delay for exponential reconnect backoff. Default 250ms.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum reconnect backoff delay. Default 5s.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional caller-owned <see cref="HttpClient"/>. When null, Sonora uses a shared internal client.
    /// The caller remains responsible for disposing a supplied client.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Maximum response header aggregate size accepted. Default 64 KiB.</summary>
    public int MaxResponseHeadersLength { get; init; } = 64 * 1024;

    /// <summary>Validates and returns a clamped copy safe for runtime use.</summary>
    public HttpStreamingOptions Validate()
    {
        const int hardMaxBuffer = 16 * 1024 * 1024;
        var maxBuffer = Math.Clamp(MaxNetworkBufferBytes, 16 * 1024, hardMaxBuffer);
        var initial = Math.Clamp(InitialBufferBytes, 1, maxBuffer);
        var rebuffer = Math.Clamp(RebufferThresholdBytes, 1, maxBuffer);
        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }

        if (ReadIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReadIdleTimeout));
        }

        if (MaxRedirects is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRedirects));
        }

        if (MaxReconnectAttempts is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts));
        }

        if (RetryBaseDelay < TimeSpan.Zero || RetryMaxDelay < RetryBaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryMaxDelay));
        }

        if (MaxResponseHeadersLength is < 1024 or > 256 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResponseHeadersLength));
        }

        return new HttpStreamingOptions
        {
            InitialBufferBytes = initial,
            MaxNetworkBufferBytes = maxBuffer,
            RebufferThresholdBytes = rebuffer,
            ConnectTimeout = ConnectTimeout,
            ReadIdleTimeout = ReadIdleTimeout,
            MaxRedirects = MaxRedirects,
            MaxReconnectAttempts = MaxReconnectAttempts,
            RetryBaseDelay = RetryBaseDelay,
            RetryMaxDelay = RetryMaxDelay,
            HttpClient = HttpClient,
            MaxResponseHeadersLength = MaxResponseHeadersLength,
        };
    }
}
