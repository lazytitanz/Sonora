using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Seeking;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

/// <summary>
/// Active-track decode failures must surface <see cref="AudioPlayer.ErrorOccurred"/>,
/// stop transport, and never raise <see cref="AudioPlayer.PlaybackCompleted"/>.
/// </summary>
public sealed class DecodeErrorSemanticsTests
{
    [Fact]
    public async Task ActiveDecodeFailure_RaisesErrorStopsAndSkipsCompletion()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var decoder = new ThrowingAfterReadDecoder(format, throwAfterSamples: format.ChannelCount * 128);
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();

        var completed = 0;
        var errors = 0;
        Exception? lastError = null;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);
        player.ErrorOccurred += (_, ex) =>
        {
            Interlocked.Increment(ref errors);
            lastError = ex;
        };

        await player.LoadAsync("track-a.wav");
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref errors) == 0 && sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20);
        }

        await Task.Delay(150);
        Assert.True(errors >= 1, "Expected ErrorOccurred.");
        Assert.Equal(0, completed);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.NotNull(lastError);
    }

    [Fact]
    public async Task AfterDecodeFailure_NextLoadCanPlay()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var call = 0;
        var factory = new FakeAudioDecoderFactory((_, _) =>
        {
            var n = Interlocked.Increment(ref call);
            IAudioDecoder decoder = n == 1
                ? new ThrowingAfterReadDecoder(format, throwAfterSamples: format.ChannelCount * 64)
                : FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(150));
            return Task.FromResult(decoder);
        });

        using var engine = new AudioEngine(
            factory,
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();

        var errors = 0;
        var completed = 0;
        player.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("bad.wav");
        player.Play();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref errors) == 0 && sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20);
        }

        Assert.True(errors >= 1);
        Assert.Equal(PlaybackState.Stopped, player.State);

        await player.LoadAsync("good.wav");
        player.Play();
        sw.Restart();
        while (Volatile.Read(ref completed) == 0 && sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20);
        }

        Assert.True(completed >= 1, "Second track should complete normally after a prior decode error.");
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task PreloadDecodeFailure_DoesNotStopActiveTrack()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var factory = new FakeAudioDecoderFactory((path, _) =>
        {
            if (path.Contains("next", StringComparison.OrdinalIgnoreCase))
            {
                throw new DecoderException("Simulated preload open failure.");
            }

            return Task.FromResult<IAudioDecoder>(
                FakeAudioDecoder.CreateTone(format, TimeSpan.FromMilliseconds(400)));
        });

        using var engine = new AudioEngine(
            factory,
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();

        var errors = 0;
        var completed = 0;
        player.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync("active.wav");
        player.Play();
        await player.PreloadNextAsync("next-bad.wav");

        Assert.True(Volatile.Read(ref errors) >= 1, "Preload open failure should raise ErrorOccurred.");
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(NextTrackState.Failed, player.NextTrackState);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref completed) == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.True(completed >= 1, "Active track should still complete.");
    }
}

/// <summary>Throws <see cref="DecoderException"/> after emitting a bounded amount of PCM (0 = immediate).</summary>
internal sealed class ThrowingAfterReadDecoder : IAudioDecoder
{
    private readonly int _throwAfterSamples;
    private int _produced;
    private bool _disposed;

    public ThrowingAfterReadDecoder(AudioFormat format, int throwAfterSamples)
    {
        Format = format;
        _throwAfterSamples = Math.Max(0, throwAfterSamples);
        Duration = TimeSpan.FromSeconds(5);
        HasKnownDuration = true;
        CanSeek = true;
    }

    public AudioFormat Format { get; }
    public TimeSpan Duration { get; }
    public bool HasKnownDuration { get; }
    public bool CanSeek { get; }

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_produced >= _throwAfterSamples)
        {
            throw new DecoderException("Simulated decode failure.");
        }

        var channels = Format.ChannelCount;
        var remaining = _throwAfterSamples - _produced;
        var toWrite = Math.Min(destination.Length, remaining);
        toWrite -= toWrite % channels;
        if (toWrite <= 0)
        {
            throw new DecoderException("Simulated decode failure.");
        }

        destination[..toWrite].Clear();
        _produced += toWrite;
        return toWrite;
    }

    public AudioSeekResult Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _produced = 0;
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        return AudioSeekResult.Exact(clamped, clamped);
    }

    public void Dispose() => _disposed = true;
}
