using System.Net;
using System.Net.Http.Headers;
using Sonora.Audio.Network;
using Sonora.Audio.Playback;
using Sonora.Audio.Sources;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.Tests.Fakes;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Verifies real Windows decoders against in-process HTTP (no external network, no WASAPI hardware).
/// </summary>
public sealed class HttpStreamingFormatTests
{
    public static TheoryData<string> RangeCapableFixtures => new()
    {
        "tone-1s.mp3",
        "tone-1s.flac",
        "tone-1s.ogg",
        "tone-1s.opus",
        "tone-1s.aac",
        "tone-1s.wma",
        "tone-1s.m4a",
        "tone-1s-opus.webm",
    };

    public static TheoryData<string> ForwardOnlyMustWork => new()
    {
        "tone-1s.ogg",
        "tone-1s.opus",
    };

    public static TheoryData<string> ForwardOnlyMayRequireSeekable => new()
    {
        "tone-1s.mp3",
        "tone-1s.flac",
        "tone-1s.aac",
        "tone-1s.m4a",
        "tone-1s.wma",
        "tone-1s-opus.webm",
    };

    [Theory]
    [MemberData(nameof(RangeCapableFixtures))]
    public async Task HttpRange_Decode_ProducesPcm(string fixtureName)
    {
        var path = FixturePath(fixtureName);
        Assert.True(File.Exists(path), $"Missing fixture {path}");

        var bytes = await File.ReadAllBytesAsync(path);
        using var client = new HttpClient(RangeAwareHandler(bytes, ContentTypeFor(fixtureName)));
        var factory = new WindowsAudioDecoderFactory(new HttpStreamingOptions
        {
            HttpClient = client,
            InitialBufferBytes = 4096,
            MaxNetworkBufferBytes = 256 * 1024,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        });

        using var decoder = await factory.CreateAsync(
            HttpAudioSource.Create(new Uri($"http://fixtures.local/{fixtureName}")));

        Assert.True(decoder.Format.SampleRate > 0);
        Assert.True(decoder.Format.ChannelCount > 0);
        Assert.True(decoder.CanSeek);

        var buffer = new float[decoder.Format.ChannelCount * 1024];
        var total = 0;
        for (var i = 0; i < 32; i++)
        {
            var n = decoder.Read(buffer);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        Assert.True(total > 0, $"{fixtureName}: expected PCM samples over HTTP range stream");
    }

    [Theory]
    [MemberData(nameof(ForwardOnlyMustWork))]
    public async Task HttpForwardOnly_OggFamily_ProducesPcm(string fixtureName)
    {
        var path = FixturePath(fixtureName);
        Assert.True(File.Exists(path), $"Missing fixture {path}");
        var bytes = await File.ReadAllBytesAsync(path);
        using var client = new HttpClient(IgnoreRangeHandler(bytes, ContentTypeFor(fixtureName)));
        var factory = new WindowsAudioDecoderFactory(new HttpStreamingOptions
        {
            HttpClient = client,
            InitialBufferBytes = 4096,
            MaxNetworkBufferBytes = 256 * 1024,
        });

        using var decoder = await factory.CreateAsync(
            HttpAudioSource.Create(new Uri($"http://fixtures.local/{fixtureName}")));
        // Transport is forward-only; some container readers may still report CanSeek.
        // Duration must not be fabricated for unknown-length progressive opens.
        var buffer = new float[4096];
        var n = decoder.Read(buffer);
        Assert.True(n > 0, $"{fixtureName} forward-only must decode");
    }

    [Theory]
    [MemberData(nameof(ForwardOnlyMayRequireSeekable))]
    public async Task HttpForwardOnly_MfFormats_DocumentCompatibility(string fixtureName)
    {
        var path = FixturePath(fixtureName);
        if (!File.Exists(path))
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(path);
        using var client = new HttpClient(IgnoreRangeHandler(bytes, ContentTypeFor(fixtureName)));
        var factory = new WindowsAudioDecoderFactory(new HttpStreamingOptions
        {
            HttpClient = client,
            InitialBufferBytes = 4096,
            MaxNetworkBufferBytes = 256 * 1024,
        });

        try
        {
            using var decoder = await factory.CreateAsync(
                HttpAudioSource.Create(new Uri($"http://fixtures.local/{fixtureName}")));
            var buffer = new float[4096];
            var n = decoder.Read(buffer);
            CompatibilityLog.Add($"{fixtureName}/forward-only: OK firstRead={n} CanSeek={decoder.CanSeek}");
        }
        catch (Exception ex) when (
            ex is UnsupportedFormatException
                or InvalidAudioException
                or NotSupportedException
                or DecoderException
                or NetworkAudioException
                or ArgumentException)
        {
            CompatibilityLog.Add($"{fixtureName}/forward-only: UNSUPPORTED ({ex.GetType().Name})");
        }
    }

    [Fact]
    public async Task HttpRange_Mp3_LoadsIntoPlayer()
    {
        var path = FixturePath("tone-1s.mp3");
        Assert.True(File.Exists(path));
        var bytes = await File.ReadAllBytesAsync(path);
        using var client = new HttpClient(RangeAwareHandler(bytes, "audio/mpeg"));
        var httpOptions = new HttpStreamingOptions
        {
            HttpClient = client,
            InitialBufferBytes = 8192,
            MaxNetworkBufferBytes = 256 * 1024,
        };
        var factory = new WindowsAudioDecoderFactory(httpOptions);
        using var engine = new AudioEngine(
            factory,
            new NoOpAudioOutputFactory(),
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.FromMilliseconds(20),
                HttpStreaming = httpOptions,
            });
        using var player = engine.CreatePlayer();
        await player.LoadAsync(HttpAudioSource.Create("http://fixtures.local/tone-1s.mp3"));
        Assert.True(player.HasSource);
        Assert.Equal("http://fixtures.local/tone-1s.mp3", player.SourcePath);
        Assert.True(player.Duration > TimeSpan.Zero || !player.HasKnownDuration);
    }

    /// <summary>Collected forward-only MF results for the release matrix.</summary>
    public static List<string> CompatibilityLog { get; } = new();

    private static string FixturePath(string name)
    {
        var committed = Path.Combine(AppContext.BaseDirectory, "CommittedFixtures", name);
        if (File.Exists(committed))
        {
            return committed;
        }

        return Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    }

    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".opus" => "audio/opus",
        ".wav" => "audio/wav",
        ".aac" => "audio/aac",
        ".wma" => "audio/x-ms-wma",
        ".m4a" => "audio/mp4",
        ".webm" => "audio/webm",
        _ => "application/octet-stream",
    };

    private static StubHandler RangeAwareHandler(byte[] body, string contentType)
        => new(req =>
        {
            if (req.Headers.Range?.Ranges.FirstOrDefault() is { From: long from })
            {
                long to = req.Headers.Range.Ranges.First().To ?? (body.Length - 1);
                to = Math.Min(to, body.Length - 1);
                if (from >= body.Length)
                {
                    return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                }

                var len = (int)(to - from + 1);
                var slice = new byte[len];
                Buffer.BlockCopy(body, (int)from, slice, 0, len);
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice),
                };
                partial.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
                partial.Headers.ETag = EntityTagHeaderValue.Parse("\"fixture\"");
                return partial;
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            ok.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            ok.Content.Headers.ContentLength = body.Length;
            ok.Headers.ETag = EntityTagHeaderValue.Parse("\"fixture\"");
            return ok;
        });

    private static StubHandler IgnoreRangeHandler(byte[] body, string contentType)
        => new(_ =>
        {
            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            ok.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            ok.Content.Headers.ContentLength = body.Length;
            return ok;
        });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
