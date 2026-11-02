using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Sonora.Audio.Network;

/// <summary>
/// Bounded HTTP body stream for audio decoding.
/// Temporary stalls block the calling (decode) thread and never return 0;
/// 0 is reserved for genuine EOF after the response completes.
/// </summary>
internal sealed class HttpContentByteStream : Stream
{
    private readonly Uri _uri;
    private readonly HttpStreamingOptions _options;
    private readonly HttpClient _client;
    private readonly BoundedByteRing _ring;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _sync = new();
    private readonly Action<NetworkTransportStatus>? _onStatus;
    private readonly Action<int>? _onBufferedBytes;

    /// <summary>Raised when transport status changes.</summary>
    public event EventHandler? StatusChanged;

    private Task? _producer;
    private HttpResponseMessage? _response;
    private Stream? _responseStream;
    private HttpResourceIdentity? _identity;
    private long _position;
    private long? _length;
    private bool _canSeek;
    private bool _opened;
    private bool _disposed;
    private int _seekGeneration;
    private NetworkTransportStatus _status = NetworkTransportStatus.Idle;
    private string? _contentType;

    public HttpContentByteStream(
        Uri uri,
        HttpStreamingOptions options,
        Action<NetworkTransportStatus>? onStatus = null,
        Action<int>? onBufferedBytes = null)
    {
        _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        _options = (options ?? HttpStreamingOptions.Default).Validate();
        _client = HttpAudioClientFactory.GetOrCreate(_options);
        _ring = new BoundedByteRing(_options.MaxNetworkBufferBytes);
        _onStatus = onStatus;
        _onBufferedBytes = onBufferedBytes;
    }

    public string? ContentType => _contentType;
    public NetworkTransportStatus Status => _status;
    public int BufferedBytes => _ring.Count;
    public bool HasStrongValidator => _identity?.HasStrongValidator == true;
    public HttpResourceIdentity? Identity => _identity;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => _canSeek && !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length ?? throw new NotSupportedException("Content length is unknown.");
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>Opens the resource, probes range support, and starts the producer.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_opened)
        {
            return;
        }

        SetStatus(NetworkTransportStatus.Connecting);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        linked.CancelAfter(_options.ConnectTimeout);

        try
        {
            var probe = await SendAsync(
                createRequest: () => CreateRequest(HttpMethod.Get, rangeStart: 0, rangeEndInclusive: 0),
                cancellationToken: linked.Token).ConfigureAwait(false);

            try
            {
                ValidateHeaderBudget(probe);
                if (probe.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (!TryParseContentRange(probe, out var start, out var end, out var total)
                        || start != 0
                        || end < start
                        || total is null or <= 0)
                    {
                        probe.Dispose();
                        throw new NetworkAudioException(
                            $"Invalid Content-Range from '{HttpUriSafety.Describe(_uri)}'.");
                    }

                    _contentType = probe.Content.Headers.ContentType?.MediaType;

                    // Live Icecast/SHOUTcast mounts lie about Content-Range — reopen without Range.
                    if (HttpLiveStreamProbe.IsNonSeekablePartialContent(probe, total))
                    {
                        probe.Dispose();
                        _identity = new HttpResourceIdentity(contentLength: null, strongETag: null, lastModified: null);
                        _length = null;
                        _canSeek = false;
                        await StartProducerFromAsync(offset: 0, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _identity = ParseIdentity(probe);
                        _length = total;
                        _canSeek = true;
                        // Discard the single probe byte body and reopen a full-range stream from 0.
                        probe.Dispose();
                        await StartProducerFromAsync(offset: 0, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (probe.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignored Range or does not support it — forward-only.
                    _identity = ParseIdentity(probe);
                    _length = probe.Content.Headers.ContentLength;
                    _canSeek = false;
                    _contentType = probe.Content.Headers.ContentType?.MediaType;
                    _response = probe;
                    _responseStream = await probe.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                    _position = 0;
                    _opened = true;
                    SetStatus(NetworkTransportStatus.Buffering);
                    _producer = Task.Run(() => ProducerLoopAsync(_lifetimeCts.Token), CancellationToken.None);
                }
                else
                {
                    var code = (int)probe.StatusCode;
                    probe.Dispose();
                    throw new NetworkAudioException(
                        $"HTTP {code} while opening '{HttpUriSafety.Describe(_uri)}'.");
                }
            }
            catch
            {
                SetStatus(NetworkTransportStatus.Failed);
                throw;
            }

            await WaitForInitialBufferAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(NetworkTransportStatus.Ready);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested && !_lifetimeCts.IsCancellationRequested)
        {
            SetStatus(NetworkTransportStatus.Failed);
            throw new NetworkAudioException(
                $"Timed out connecting to '{HttpUriSafety.Describe(_uri)}'.",
                ex);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        try
        {
            // Stall blocks the decode worker only — never returns 0 until genuine EOF.
            var read = _ring.Read(buffer, _lifetimeCts.Token);
            if (read > 0)
            {
                _position += read;
                ReportBuffered();
                MaybeSetReadyOrBuffering();
                return read;
            }

            // Genuine EOF
            SetStatus(NetworkTransportStatus.Ended);
            return 0;
        }
        catch (OperationCanceledException) when (_disposed || _lifetimeCts.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(HttpContentByteStream));
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_canSeek || _length is null)
        {
            throw new NotSupportedException("The HTTP source is not seekable.");
        }

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length.Value + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0 || target > _length.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (target == _position)
        {
            return _position;
        }

        SeekToByteOffset(target);
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            try
            {
                _lifetimeCts.Cancel();
            }
            catch
            {
                // ignored
            }

            _ring.Dispose();
            try
            {
                _producer?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignored
            }

            _responseStream?.Dispose();
            _response?.Dispose();
            _lifetimeCts.Dispose();
            SetStatus(NetworkTransportStatus.Idle);
        }

        base.Dispose(disposing);
    }

    private void SeekToByteOffset(long target)
    {
        var generation = Interlocked.Increment(ref _seekGeneration);
        SetStatus(NetworkTransportStatus.Buffering);

        // Stop current producer and discard buffered bytes from the prior version/offset.
        CancelProducerAndWait();
        _ring.Clear();

        try
        {
            StartProducerFromAsync(target, _lifetimeCts.Token).GetAwaiter().GetResult();
            if (generation != Volatile.Read(ref _seekGeneration))
            {
                return;
            }

            _position = target;
            WaitForInitialBufferAsync(_lifetimeCts.Token).GetAwaiter().GetResult();
            SetStatus(NetworkTransportStatus.Ready);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            SetStatus(NetworkTransportStatus.Failed);
            throw ex is NetworkAudioException
                ? ex
                : new NetworkAudioException(
                    $"HTTP seek failed for '{HttpUriSafety.Describe(_uri)}'.",
                    ex);
        }
    }

    private async Task StartProducerFromAsync(long offset, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        linked.CancelAfter(_options.ConnectTimeout);

        // Forward-only opens must not send Range: bytes=0- — Icecast live mounts often 403 or stall on it.
        long? rangeStart = offset > 0 || _canSeek ? offset : null;
        long? rangeEnd = null;
        var response = await SendAsync(
            () => CreateRequest(HttpMethod.Get, rangeStart, rangeEnd),
            linked.Token).ConfigureAwait(false);

        try
        {
            ValidateHeaderBudget(response);
            var identity = ParseIdentity(response);

            if (offset > 0 || _canSeek)
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    response.Dispose();
                    throw new NetworkAudioException(
                        $"HTTP 416 Range Not Satisfiable for '{HttpUriSafety.Describe(_uri)}'.");
                }

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    response.Dispose();
                    throw new NetworkAudioException(
                        $"Expected HTTP 206 for ranged GET of '{HttpUriSafety.Describe(_uri)}', got {(int)response.StatusCode}.");
                }

                if (!TryParseContentRange(response, out var start, out _, out var total)
                    || start != offset)
                {
                    response.Dispose();
                    throw new NetworkAudioException(
                        $"Content-Range mismatch for '{HttpUriSafety.Describe(_uri)}'.");
                }

                var rangedIdentity = identity.WithLength(total ?? identity.ContentLength);
                if (_identity is not null && !_identity.Matches(rangedIdentity))
                {
                    response.Dispose();
                    throw new NetworkAudioException(
                        $"HTTP resource identity changed for '{HttpUriSafety.Describe(_uri)}'; refusing to mix versions.");
                }

                if (_identity is null)
                {
                    _identity = rangedIdentity;
                }

                _length = total ?? _length;
            }
            else if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
            {
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new NetworkAudioException(
                    $"HTTP {code} while reading '{HttpUriSafety.Describe(_uri)}'.");
            }

            _response?.Dispose();
            _responseStream?.Dispose();
            _response = response;
            _responseStream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            _opened = true;
            SetStatus(NetworkTransportStatus.Buffering);
            _producer = Task.Run(() => ProducerLoopAsync(_lifetimeCts.Token), CancellationToken.None);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task ProducerLoopAsync(CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _seekGeneration);
        var buffer = new byte[8192];
        var reconnects = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (generation != Volatile.Read(ref _seekGeneration))
                {
                    return; // superseded by seek/reconnect
                }

                var stream = _responseStream;
                if (stream is null)
                {
                    break;
                }

                int read;
                try
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readCts.CancelAfter(_options.ReadIdleTimeout);
                    read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is IOException or HttpRequestException or TaskCanceledException
                    && !cancellationToken.IsCancellationRequested)
                {
                    if (generation != Volatile.Read(ref _seekGeneration))
                    {
                        return;
                    }

                    if (!_canSeek || _length is null || reconnects >= _options.MaxReconnectAttempts)
                    {
                        FailProducer(new NetworkAudioException(
                            $"HTTP read failed for '{HttpUriSafety.Describe(_uri)}'.",
                            ex));
                        return;
                    }

                    reconnects++;
                    SetStatus(NetworkTransportStatus.Reconnecting);
                    await DelayBackoffAsync(reconnects, cancellationToken).ConfigureAwait(false);
                    await ReconnectAtPositionAsync(cancellationToken).ConfigureAwait(false);
                    generation = Volatile.Read(ref _seekGeneration);
                    continue;
                }

                if (generation != Volatile.Read(ref _seekGeneration))
                {
                    return;
                }

                if (read <= 0)
                {
                    _ring.MarkCompleted();
                    ReportBuffered();
                    return;
                }

                reconnects = 0;
                _ring.Write(buffer.AsSpan(0, read), cancellationToken);
                ReportBuffered();
                MaybeSetReadyOrBuffering();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // lifetime cancelled
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailProducer(ex is NetworkAudioException
                ? ex
                : new NetworkAudioException(
                    $"HTTP transport failed for '{HttpUriSafety.Describe(_uri)}'.",
                    ex));
        }
    }

    private async Task ReconnectAtPositionAsync(CancellationToken cancellationToken)
    {
        // Next unread absolute byte is the logical stream position plus bytes still buffered.
        // Producer reconnects from the server-side cursor: current Position is consumer-side;
        // buffered bytes have already been downloaded. Resume at Position + BufferedBytes.
        var resumeAt = _position + _ring.Count;
        CancelResponseOnly();
        _ring.Clear(); // drop possibly torn buffer; consumer Position unchanged
        // After clear, resume at consumer position (torn data discarded).
        resumeAt = _position;
        await StartProducerFromAsync(resumeAt, cancellationToken).ConfigureAwait(false);
    }

    private async Task DelayBackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var ms = (int)Math.Min(
            _options.RetryMaxDelay.TotalMilliseconds,
            _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        // Deterministic jitter from attempt + tick for testability without Random static races.
        var jitter = (int)(ms * 0.2 * ((Environment.TickCount64 + attempt) % 100) / 100.0);
        await Task.Delay(ms + jitter, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForInitialBufferAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = Math.Min(_options.InitialBufferBytes, _options.MaxNetworkBufferBytes);
        while (_ring.Count < target && !_ring.IsCompleted && sw.Elapsed < _options.ConnectTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        var uri = _uri;
        string? authorization = null;
        for (var redirect = 0; redirect <= _options.MaxRedirects; redirect++)
        {
            using var request = createRequest();
            request.RequestUri = uri;
            if (authorization is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            ApplyIfRange(request);

            HttpResponseMessage response;
            try
            {
                response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("HTTP open was cancelled.", ex, cancellationToken);
                }

                throw new NetworkAudioException(
                    $"Failed to connect to '{HttpUriSafety.Describe(_uri)}'.",
                    ex);
            }

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    throw new NetworkAudioException(
                        $"Redirect without Location from '{HttpUriSafety.Describe(_uri)}'.");
                }

                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (authorization is not null && !HttpUriSafety.IsSameOrigin(uri, next))
                {
                    authorization = null; // never forward credentials cross-origin
                }

                // Capture Authorization only from the original request URI scope if present on HttpClient defaults — we don't set it ourselves.
                uri = next;
                continue;
            }

            return response;
        }

        throw new NetworkAudioException(
            $"Too many redirects while opening '{HttpUriSafety.Describe(_uri)}'.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, long? rangeStart, long? rangeEndInclusive)
    {
        var request = new HttpRequestMessage(method, _uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (rangeStart is long start)
        {
            request.Headers.Range = rangeEndInclusive is long end
                ? new RangeHeaderValue(start, end)
                : new RangeHeaderValue(start, null);
        }

        return request;
    }

    private void ApplyIfRange(HttpRequestMessage request)
    {
        if (request.Headers.Range is null || _identity?.StrongETag is null)
        {
            return;
        }

        // If-Range with strong ETag: server returns 200 full body if representation changed.
        request.Headers.IfRange = new RangeConditionHeaderValue(
            EntityTagHeaderValue.Parse(_identity.StrongETag));
    }

    private static HttpResourceIdentity ParseIdentity(HttpResponseMessage response)
    {
        var length = response.Content.Headers.ContentLength;
        if (response.StatusCode == HttpStatusCode.PartialContent
            && TryParseContentRange(response, out _, out _, out var total))
        {
            length = total ?? length;
        }

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
            // Fall back to raw parse for non-standard servers.
            if (!response.Content.Headers.TryGetValues("Content-Range", out var values))
            {
                return false;
            }

            var raw = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var body = raw["bytes ".Length..].Trim();
            var slash = body.IndexOf('/');
            if (slash < 0)
            {
                return false;
            }

            var rangePart = body[..slash];
            var totalPart = body[(slash + 1)..];
            var dash = rangePart.IndexOf('-');
            if (dash < 0
                || !long.TryParse(rangePart[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
                || !long.TryParse(rangePart[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
            {
                return false;
            }

            if (totalPart != "*"
                && long.TryParse(totalPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            {
                total = t;
            }

            return true;
        }

        start = header.From ?? 0;
        end = header.To ?? 0;
        total = header.HasLength ? header.Length : null;
        return true;
    }

    private void ValidateHeaderBudget(HttpResponseMessage response)
    {
        // Approximate: sum of header string lengths.
        long total = 0;
        foreach (var h in response.Headers)
        {
            total += h.Key.Length;
            foreach (var v in h.Value)
            {
                total += v.Length;
            }
        }

        foreach (var h in response.Content.Headers)
        {
            total += h.Key.Length;
            foreach (var v in h.Value)
            {
                total += v.Length;
            }
        }

        if (total > _options.MaxResponseHeadersLength)
        {
            throw new NetworkAudioException(
                $"HTTP response headers exceeded the configured limit for '{HttpUriSafety.Describe(_uri)}'.");
        }
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private void CancelProducerAndWait()
    {
        CancelResponseOnly();
        try
        {
            _producer?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }

        _producer = null;
    }

    private void CancelResponseOnly()
    {
        try
        {
            _responseStream?.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            _response?.Dispose();
        }
        catch
        {
            // ignored
        }

        _responseStream = null;
        _response = null;
    }

    private void FailProducer(Exception error)
    {
        _ring.MarkFailed(error);
        SetStatus(NetworkTransportStatus.Failed);
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
            _onStatus?.Invoke(status);
        }
        catch
        {
            // ignored
        }

        RaiseStatusChanged();
    }

    private void ReportBuffered()
    {
        try
        {
            _onBufferedBytes?.Invoke(_ring.Count);
        }
        catch
        {
            // ignored
        }

        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // ignored
        }
    }

    private void MaybeSetReadyOrBuffering()
    {
        if (_status is NetworkTransportStatus.Ended or NetworkTransportStatus.Failed or NetworkTransportStatus.Idle)
        {
            return;
        }

        if (_ring.Count < _options.RebufferThresholdBytes && !_ring.IsCompleted)
        {
            SetStatus(NetworkTransportStatus.Buffering);
        }
        else if (_status is NetworkTransportStatus.Buffering or NetworkTransportStatus.Connecting or NetworkTransportStatus.Reconnecting)
        {
            SetStatus(NetworkTransportStatus.Ready);
        }
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }
}

