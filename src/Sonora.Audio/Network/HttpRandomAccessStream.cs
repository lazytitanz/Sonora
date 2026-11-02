using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Sonora.Audio.Network;

/// <summary>
/// Seekable HTTP stream backed by validated byte-range requests and a bounded sliding window cache.
/// Suitable for Media Foundation and other APIs that perform synchronous random reads.
/// Temporary cache misses block the caller (decode worker) and never return 0 until genuine EOF past Length.
/// </summary>
internal sealed class HttpRandomAccessStream : Stream
{
    private readonly Uri _uri;
    private readonly HttpStreamingOptions _options;
    private readonly HttpClient _client;
    private readonly object _sync = new();
    private readonly byte[] _window;
    private HttpResourceIdentity _identity;
    private long _windowStart;
    private int _windowLength;
    private long _position;
    private readonly long _length;
    private bool _disposed;
    private NetworkTransportStatus _status = NetworkTransportStatus.Ready;
    private string? _contentType;

    private HttpRandomAccessStream(
        Uri uri,
        HttpStreamingOptions options,
        HttpClient client,
        HttpResourceIdentity identity,
        long length,
        string? contentType)
    {
        _uri = uri;
        _options = options;
        _client = client;
        _identity = identity;
        _length = length;
        _contentType = contentType;
        _window = new byte[options.MaxNetworkBufferBytes];
        _windowStart = 0;
        _windowLength = 0;
    }

    public string? ContentType => _contentType;
    public NetworkTransportStatus Status => _status;
    public int BufferedBytes
    {
        get
        {
            lock (_sync)
            {
                return _windowLength;
            }
        }
    }

    public bool HasStrongValidator => _identity.HasStrongValidator;
    public HttpResourceIdentity Identity => _identity;

    public event EventHandler? StatusChanged;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public static async Task<HttpRandomAccessStream> OpenAsync(
        Uri uri,
        HttpStreamingOptions options,
        CancellationToken cancellationToken = default)
    {
        options = options.Validate();
        var client = HttpAudioClientFactory.GetOrCreate(options);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(options.ConnectTimeout);

        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
        probeRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        HttpResponseMessage probe;
        try
        {
            probe = await client
                .SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new NetworkAudioException($"Failed to connect to '{HttpUriSafety.Describe(uri)}'.", ex);
        }

        using (probe)
        {
            if (probe.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new NetworkAudioException(
                    $"HTTP range probe for '{HttpUriSafety.Describe(uri)}' did not return 206.");
            }

            if (!TryParseContentRange(probe, out var start, out _, out var total)
                || start != 0
                || total is null or <= 0)
            {
                throw new NetworkAudioException(
                    $"Invalid Content-Range from '{HttpUriSafety.Describe(uri)}'.");
            }

            // Icecast/SHOUTcast live mounts advertise a fake Content-Range; do not treat as seekable.
            if (HttpLiveStreamProbe.IsNonSeekablePartialContent(probe, total))
            {
                throw new NetworkAudioException(
                    $"HTTP resource '{HttpUriSafety.Describe(uri)}' looks like a live stream; byte-range random access is unavailable.");
            }

            var identity = ParseIdentity(probe, total);
            var contentType = probe.Content.Headers.ContentType?.MediaType;
            return new HttpRandomAccessStream(uri, options, client, identity, total.Value, contentType);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        lock (_sync)
        {
            if (_position >= _length)
            {
                SetStatus(NetworkTransportStatus.Ended);
                return 0;
            }

            EnsureWindowContains_NoLock(_position);
            var windowOffset = (int)(_position - _windowStart);
            var available = _windowLength - windowOffset;
            var toCopy = Math.Min(buffer.Length, available);
            toCopy = (int)Math.Min(toCopy, _length - _position);
            if (toCopy <= 0)
            {
                return 0;
            }

            _window.AsSpan(windowOffset, toCopy).CopyTo(buffer);
            _position += toCopy;
            return toCopy;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0 || target > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _position = target;
        SetStatus(NetworkTransportStatus.Ready);
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        SetStatus(NetworkTransportStatus.Idle);
        base.Dispose(disposing);
    }

    private void EnsureWindowContains_NoLock(long absoluteOffset)
    {
        if (absoluteOffset >= _windowStart && absoluteOffset < _windowStart + _windowLength)
        {
            return;
        }

        SetStatus(NetworkTransportStatus.Buffering);
        var fetchStart = absoluteOffset;
        var fetchLen = (int)Math.Min(_window.Length, _length - fetchStart);
        if (fetchLen <= 0)
        {
            return;
        }

        var fetched = FetchRange(fetchStart, fetchStart + fetchLen - 1);
        if (fetched.Length == 0)
        {
            throw new NetworkAudioException(
                $"HTTP range fetch returned no data for '{HttpUriSafety.Describe(_uri)}'.");
        }

        fetched.AsSpan().CopyTo(_window);
        _windowStart = fetchStart;
        _windowLength = fetched.Length;
        SetStatus(NetworkTransportStatus.Ready);
    }

    private byte[] FetchRange(long start, long endInclusive)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(start, endInclusive);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (_identity.StrongETag is not null)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(
                EntityTagHeaderValue.Parse(_identity.StrongETag));
        }

        using var cts = new CancellationTokenSource(_options.ConnectTimeout);
        HttpResponseMessage response;
        try
        {
            // Sync-over-async: MF and sniff paths require synchronous Stream.Read.
            response = _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            SetStatus(NetworkTransportStatus.Failed);
            throw new NetworkAudioException(
                $"HTTP range read failed for '{HttpUriSafety.Describe(_uri)}'.",
                ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                // If-Range failed (representation changed) or server ignored range.
                SetStatus(NetworkTransportStatus.Failed);
                throw new NetworkAudioException(
                    $"HTTP resource identity changed or range ignored for '{HttpUriSafety.Describe(_uri)}'.");
            }

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                SetStatus(NetworkTransportStatus.Failed);
                throw new NetworkAudioException(
                    $"HTTP 416 for '{HttpUriSafety.Describe(_uri)}'.");
            }

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                SetStatus(NetworkTransportStatus.Failed);
                throw new NetworkAudioException(
                    $"Expected HTTP 206 for '{HttpUriSafety.Describe(_uri)}', got {(int)response.StatusCode}.");
            }

            if (!TryParseContentRange(response, out var respStart, out _, out var total)
                || respStart != start
                || (total is long t && t != _length))
            {
                SetStatus(NetworkTransportStatus.Failed);
                throw new NetworkAudioException(
                    $"Content-Range mismatch for '{HttpUriSafety.Describe(_uri)}'.");
            }

            var identity = ParseIdentity(response, total ?? _length);
            if (!_identity.Matches(identity))
            {
                SetStatus(NetworkTransportStatus.Failed);
                throw new NetworkAudioException(
                    $"HTTP resource identity changed for '{HttpUriSafety.Describe(_uri)}'; refusing to mix versions.");
            }

            if (identity.HasStrongValidator)
            {
                _identity = identity.WithLength(_length);
            }

            using var body = response.Content.ReadAsStreamAsync(cts.Token)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            var buffer = new byte[endInclusive - start + 1];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var n = body.ReadAsync(buffer.AsMemory(totalRead), cts.Token)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
                if (n <= 0)
                {
                    break;
                }

                totalRead += n;
            }

            if (totalRead == buffer.Length)
            {
                return buffer;
            }

            var trimmed = new byte[totalRead];
            Buffer.BlockCopy(buffer, 0, trimmed, 0, totalRead);
            return trimmed;
        }
    }

    private void SetStatus(NetworkTransportStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // ignored
        }
    }

    private static HttpResourceIdentity ParseIdentity(HttpResponseMessage response, long? length)
    {
        string? etag = null;
        if (response.Headers.ETag is not null)
        {
            etag = HttpResourceIdentity.ParseStrongETag(response.Headers.ETag.ToString());
        }

        string? lastModified = null;
        if (response.Content.Headers.LastModified is DateTimeOffset dto)
        {
            lastModified = dto.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        }

        return new HttpResourceIdentity(length, etag, lastModified);
    }

    private static bool TryParseContentRange(
        HttpResponseMessage response,
        out long start,
        out long end,
        out long? total)
    {
        start = 0;
        end = 0;
        total = null;
        var header = response.Content.Headers.ContentRange;
        if (header is null || !header.HasRange || header.Unit != "bytes")
        {
            return false;
        }

        start = header.From ?? 0;
        end = header.To ?? 0;
        total = header.HasLength ? header.Length : null;
        return true;
    }
}
