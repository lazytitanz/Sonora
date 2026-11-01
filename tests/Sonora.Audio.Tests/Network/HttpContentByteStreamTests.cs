using System.Net;
using System.Net.Http.Headers;
using Sonora.Audio.Network;

namespace Sonora.Audio.Tests.Network;

public sealed class HttpContentByteStreamTests
{
    [Fact]
    public async Task Open_OkResponse_ReadsAllBytesAndEofReturnsZero()
    {
        var body = FakeHttpMessageHandler.AsciiPayload(4096);
        using var client = new HttpClient(FakeHttpMessageHandler.IgnoreRanges(body));
        var options = CreateOptions(client, initial: 128);

        await using var stream = new HttpContentByteStream(new Uri("http://test.local/a.mp3"), options);
        await stream.OpenAsync();
        Assert.False(stream.CanSeek);

        var buffer = new byte[body.Length];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer.AsSpan(total));
            Assert.True(n > 0, "Stall must not return 0 before EOF");
            total += n;
        }

        Assert.Equal(0, stream.Read(new byte[16]));
        Assert.Equal(body, buffer);
        Assert.Equal(NetworkTransportStatus.Ended, stream.Status);
    }

    [Fact]
    public async Task Open_RangeSupported_CanSeek()
    {
        var body = FakeHttpMessageHandler.AsciiPayload(8192, 'B');
        using var client = new HttpClient(FakeHttpMessageHandler.RangeAware(body));
        var options = CreateOptions(client, initial: 64);

        await using var stream = new HttpContentByteStream(new Uri("http://test.local/seek.mp3"), options);
        await stream.OpenAsync();
        Assert.True(stream.CanSeek);
        Assert.Equal(body.Length, stream.Length);
        Assert.True(stream.HasStrongValidator);

        stream.Position = 100;
        var buf = new byte[10];
        var read = stream.Read(buf);
        Assert.Equal(10, read);
        Assert.Equal(body.AsSpan(100, 10).ToArray(), buf);
    }

    [Fact]
    public async Task Dispose_CancelsLifetime()
    {
        var body = FakeHttpMessageHandler.AsciiPayload(64 * 1024);
        using var client = new HttpClient(FakeHttpMessageHandler.IgnoreRanges(body));
        var options = CreateOptions(client, initial: 64, maxBuffer: 1024);
        var stream = new HttpContentByteStream(new Uri("http://test.local/dispose.mp3"), options);
        await stream.OpenAsync();
        await stream.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[16]));
    }

    [Fact]
    public async Task GenuineEof_ReturnsZero_NotFailure()
    {
        var body = FakeHttpMessageHandler.AsciiPayload(256);
        using var client = new HttpClient(FakeHttpMessageHandler.IgnoreRanges(body));
        var options = CreateOptions(client, initial: 16);
        await using var stream = new HttpContentByteStream(new Uri("http://test.local/eof.mp3"), options);
        await stream.OpenAsync();

        var buf = new byte[512];
        var total = 0;
        int n;
        do
        {
            n = stream.Read(buf);
            total += n;
        }
        while (n > 0);

        Assert.Equal(body.Length, total);
        Assert.Equal(0, stream.Read(buf));
        Assert.Equal(NetworkTransportStatus.Ended, stream.Status);
    }

    [Fact]
    public async Task ServerError_ThrowsNetworkAudioException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
        using var client = new HttpClient(handler);
        var options = CreateOptions(client);
        await using var stream = new HttpContentByteStream(new Uri("http://test.local/err.mp3"), options);
        await Assert.ThrowsAsync<NetworkAudioException>(() => stream.OpenAsync());
    }

    [Fact]
    public async Task RangeIgnored_IsForwardOnly()
    {
        var body = FakeHttpMessageHandler.AsciiPayload(2048);
        using var client = new HttpClient(FakeHttpMessageHandler.IgnoreRanges(body));
        var options = CreateOptions(client, initial: 64);
        await using var stream = new HttpContentByteStream(new Uri("http://test.local/live.mp3"), options);
        await stream.OpenAsync();
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Seek(10, SeekOrigin.Begin));
    }

    [Fact]
    public async Task EtagChangeOnSeek_Fails()
    {
        var body = FakeHttpMessageHandler.AsciiPayload(4096);
        var call = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            call++;
            var etag = call <= 2 ? "\"v1\"" : "\"v2\"";
            if (req.Headers.Range?.Ranges.FirstOrDefault() is { From: long from })
            {
                return FakeHttpMessageHandler.Partial(body, from, etag: etag);
            }

            return FakeHttpMessageHandler.Ok(body, etag: etag);
        });
        using var client = new HttpClient(handler);
        var options = CreateOptions(client, initial: 32);
        await using var stream = new HttpContentByteStream(new Uri("http://test.local/etag.mp3"), options);
        await stream.OpenAsync();
        Assert.True(stream.CanSeek);
        var ex = Assert.Throws<NetworkAudioException>(() => stream.Seek(100, SeekOrigin.Begin));
        Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BufferRemainsBounded()
    {
        var body = FakeHttpMessageHandler.AsciiPayload(256 * 1024);
        using var client = new HttpClient(FakeHttpMessageHandler.IgnoreRanges(body));
        const int maxBuf = 16 * 1024; // matches HttpStreamingOptions hard minimum
        var options = CreateOptions(client, initial: 256, maxBuffer: maxBuf);
        await using var stream = new HttpContentByteStream(new Uri("http://test.local/bound.mp3"), options);
        await stream.OpenAsync();

        for (var i = 0; i < 50; i++)
        {
            Assert.True(stream.BufferedBytes <= maxBuf);
            var n = stream.Read(new byte[128]);
            if (n == 0)
            {
                break;
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Cancellation_AbortsOpen()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeHttpMessageHandler.Ok(FakeHttpMessageHandler.AsciiPayload(16));
        });
        using var client = new HttpClient(handler);
        var options = CreateOptions(client, connectTimeout: TimeSpan.FromSeconds(30));
        await using var stream = new HttpContentByteStream(new Uri("http://test.local/cancel.mp3"), options);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.OpenAsync(cts.Token));
    }

    [Fact]
    public async Task Stall_DoesNotReturnZero_UntilProducerCompletes()
    {
        // Slow content: first chunk immediate, second after delay — Read must block (non-zero) not EOF.
        var body = FakeHttpMessageHandler.AsciiPayload(4096);
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var content = new SlowByteArrayContent(body, firstChunk: 128, delay: TimeSpan.FromMilliseconds(200));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            return response;
        });
        using var client = new HttpClient(handler);
        var options = CreateOptions(client, initial: 64);
        await using var stream = new HttpContentByteStream(new Uri("http://test.local/stall.mp3"), options);
        await stream.OpenAsync();

        var total = 0;
        var buf = new byte[256];
        while (total < body.Length)
        {
            var n = stream.Read(buf);
            Assert.True(n > 0 || total == body.Length);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        Assert.Equal(body.Length, total);
    }

    private static HttpStreamingOptions CreateOptions(
        HttpClient client,
        int initial = 128,
        int maxBuffer = 64 * 1024,
        TimeSpan? connectTimeout = null,
        TimeSpan? readIdle = null)
        => new()
        {
            HttpClient = client,
            InitialBufferBytes = initial,
            MaxNetworkBufferBytes = maxBuffer,
            RebufferThresholdBytes = Math.Min(32, initial),
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),
            ReadIdleTimeout = readIdle ?? TimeSpan.FromSeconds(5),
            MaxReconnectAttempts = 0,
        };

    private sealed class SlowByteArrayContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly int _firstChunk;
        private readonly TimeSpan _delay;

        public SlowByteArrayContent(byte[] data, int firstChunk, TimeSpan delay)
        {
            _data = data;
            _firstChunk = firstChunk;
            _delay = delay;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var first = Math.Min(_firstChunk, _data.Length);
            await stream.WriteAsync(_data.AsMemory(0, first));
            await stream.FlushAsync();
            if (first < _data.Length)
            {
                await Task.Delay(_delay);
                await stream.WriteAsync(_data.AsMemory(first));
                await stream.FlushAsync();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }
}
