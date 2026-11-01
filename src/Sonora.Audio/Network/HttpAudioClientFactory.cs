namespace Sonora.Audio.Network;

/// <summary>Provides a shared <see cref="HttpClient"/> when the application does not supply one.</summary>
internal static class HttpAudioClientFactory
{
    private static readonly object Gate = new();
    private static HttpClient? _shared;

    public static HttpClient GetOrCreate(HttpStreamingOptions options)
    {
        if (options.HttpClient is not null)
        {
            return options.HttpClient;
        }

        lock (Gate)
        {
            if (_shared is not null)
            {
                return _shared;
            }

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // Sonora applies redirect policy (Authorization stripping).
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            };

            _shared = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts via CTS
            };
            return _shared;
        }
    }
}
