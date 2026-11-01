using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Sonora.Audio.Tests.Network;

/// <summary>Deterministic in-process HTTP handler for streaming tests. Never touches the network.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
    public int RequestCount { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : this((req, _) => Task.FromResult(handler(req)))
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return await _handler(request, cancellationToken).ConfigureAwait(false);
    }

    public static HttpResponseMessage Ok(
        byte[] body,
        string? contentType = "audio/mpeg",
        string? etag = "\"v1\"",
        bool chunked = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        if (contentType is not null)
        {
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        if (!chunked)
        {
            response.Content.Headers.ContentLength = body.Length;
        }

        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    public static HttpResponseMessage Partial(
        byte[] fullBody,
        long start,
        long? endInclusive = null,
        string? contentType = "audio/mpeg",
        string? etag = "\"v1\"")
    {
        var end = endInclusive ?? (fullBody.Length - 1);
        end = Math.Min(end, fullBody.Length - 1);
        var length = (int)(end - start + 1);
        var slice = new byte[length];
        Buffer.BlockCopy(fullBody, (int)start, slice, 0, length);

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
        response.Content.Headers.ContentLength = length;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, fullBody.Length);
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    /// <summary>Serves Range when requested; otherwise 200 with full body.</summary>
    public static FakeHttpMessageHandler RangeAware(byte[] body, string etag = "\"v1\"")
    {
        return new FakeHttpMessageHandler(req =>
        {
            if (req.Headers.Range?.Ranges.FirstOrDefault() is { } range && range.From is long from)
            {
                var to = range.To ?? (body.Length - 1);
                if (from >= body.Length)
                {
                    return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>()),
                    };
                }

                return Partial(body, from, to, etag: etag);
            }

            return Ok(body, etag: etag);
        });
    }

    public static FakeHttpMessageHandler IgnoreRanges(byte[] body)
        => new(req => Ok(body));

    public static byte[] AsciiPayload(int length, char fill = 'A')
    {
        var bytes = Encoding.ASCII.GetBytes(new string(fill, length));
        return bytes;
    }
}
