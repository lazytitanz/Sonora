using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Network;
using Sonora.Audio.Playback;
using Sonora.Audio.Seeking;
using Sonora.Audio.Sources;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests.Network;

public sealed class NetworkPlaybackTests
{
    [Fact]
    public async Task LoadAsync_HttpSource_UsesFactoryAndSetsIdentifier()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var decoder = FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(100));
        Uri? requested = null;
        var factory = new CapturingSourceFactory((source, _) =>
        {
            requested = ((HttpAudioSource)source).Uri;
            return Task.FromResult<IAudioDecoder>(decoder);
        });
        using var engine = new AudioEngine(factory, new FakeAudioOutputFactory(), new AudioEngineOptions
        {
            PrimeDuration = TimeSpan.Zero,
        });
        using var player = engine.CreatePlayer();

        var uri = new Uri("https://example.test/track.mp3");
        await player.LoadAsync(new HttpAudioSource(uri));

        Assert.Equal(uri, requested);
        Assert.Equal(uri.AbsoluteUri, player.SourcePath);
        Assert.True(player.HasSource);
        Assert.True(player.HasKnownDuration);
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task LoadAsync_StringPath_StillWorks()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(50))),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("local.wav");
        Assert.Equal("local.wav", player.SourcePath);
        Assert.IsType<FileAudioSource>(player.CurrentSource);
    }

    [Fact]
    public async Task UnknownDuration_IsDistinguishedFromZeroLength()
    {
        var format = AudioFormat.CreateFloat32(44100, 1);
        var live = new FakeAudioDecoder(format, new float[format.SampleRate], canSeek: false)
        {
            HasKnownDuration = false,
        };
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(live),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("live.mp3");
        Assert.False(player.HasKnownDuration);
    }

    [Fact]
    public async Task ZeroLengthKnown_HasKnownDurationTrue()
    {
        var format = AudioFormat.CreateFloat32(44100, 1);
        var empty = new FakeAudioDecoder(format, Array.Empty<float>())
        {
            HasKnownDuration = true,
        };
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(empty),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("empty.wav");
        Assert.True(player.HasKnownDuration);
        Assert.Equal(TimeSpan.Zero, player.Duration);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotRaisePlaybackCompleted()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var completed = 0;
        var errors = 0;
        var decoder = new ThrowingAfterReadDecoder(format, throwAfterSamples: format.ChannelCount * 256);
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);
        player.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        await player.LoadAsync(new HttpAudioSource(new Uri("http://test.local/x.mp3")));
        player.Play();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref errors) == 0 && sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20);
        }

        await Task.Delay(100);
        Assert.True(errors >= 1);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task NonSeekable_SeekThrows()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var tone = FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(200));
        var samples = new float[format.TimeToSamples(TimeSpan.FromMilliseconds(200))];
        // Rebuild non-seekable decoder with same duration samples
        var decoder = new FakeAudioDecoder(format, samples, canSeek: false);
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync(HttpAudioSource.Create("http://test.local/live.mp3"));
        Assert.Throws<NotSupportedException>(() => player.Seek(TimeSpan.FromSeconds(1)));
        tone.Dispose();
    }

    [Fact]
    public async Task Gapless_DoesNotPromoteOnDecoderStall()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(300)));
        var next = FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(300));
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            if (path.Contains("next", StringComparison.Ordinal))
            {
                return Task.FromResult<IAudioDecoder>(next);
            }

            return Task.FromResult<IAudioDecoder>(hold);
        });
        using var engine = new AudioEngine(
            factory,
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        var transitions = 0;
        var completions = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completions);

        await player.LoadAsync("a.wav");
        await player.PreloadNextAsync("next.wav");
        player.Play();
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref transitions));
        Assert.Equal(0, Volatile.Read(ref completions));
        hold.Release();
        player.Stop();
    }

    private sealed class CapturingSourceFactory : IAudioDecoderFactory
    {
        private readonly Func<AudioSource, CancellationToken, Task<IAudioDecoder>> _factory;

        public CapturingSourceFactory(Func<AudioSource, CancellationToken, Task<IAudioDecoder>> factory)
        {
            _factory = factory;
        }

        public Task<IAudioDecoder> CreateAsync(string filePath, CancellationToken cancellationToken = default)
            => _factory(FileAudioSource.Create(filePath), cancellationToken);

        public Task<IAudioDecoder> CreateAsync(AudioSource source, CancellationToken cancellationToken = default)
            => _factory(source, cancellationToken);
    }

    private sealed class ThrowingAfterReadDecoder : IAudioDecoder
    {
        private readonly FakeAudioDecoder _inner;
        private int _read;

        public ThrowingAfterReadDecoder(AudioFormat format, int throwAfterSamples)
        {
            _inner = FakeAudioDecoder.CreateTone(format, TimeSpan.FromSeconds(2));
            ThrowAfter = throwAfterSamples;
        }

        public int ThrowAfter { get; }
        public AudioFormat Format => _inner.Format;
        public TimeSpan Duration => _inner.Duration;
        public bool HasKnownDuration => true;
        public bool CanSeek => false;

        public int Read(Span<float> destination)
        {
            if (_read >= ThrowAfter)
            {
                throw new NetworkAudioException("Simulated transport failure.");
            }

            var n = _inner.Read(destination);
            _read += n;
            return n;
        }

        public AudioSeekResult Seek(TimeSpan position) => throw new NotSupportedException();
        public void Dispose() => _inner.Dispose();
    }
}
