using Sonora.Audio;
using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Windows;
using Sonora.Audio.Windows.Decoding;

namespace Sonora.Audio.Windows.Tests;

public sealed class FlacSeekTests
{
    private const int SegmentMs = 250;

    [Fact]
    public void Flac_DecodeDurationAndCanSeek()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(SegmentMs);
        using var decoder = WindowsAudioDecoder.Open(path);

        Assert.True(decoder.CanSeek);
        Assert.True(decoder.Duration > TimeSpan.FromMilliseconds(SegmentMs * 3), $"Duration was {decoder.Duration}");
        Assert.Equal(2, decoder.Format.ChannelCount);

        var buffer = new float[4096];
        Assert.True(decoder.Read(buffer) > 0);
    }

    [Theory]
    [InlineData(0, 220f)]
    [InlineData(1, 440f)]
    [InlineData(2, 880f)]
    [InlineData(3, 1760f)]
    public void Flac_SeekToSegment_ProducesExpectedFrequency(int segmentIndex, float expectedHz)
    {
        var path = FixtureHelper.EnsureSegmentedFlac(SegmentMs);
        using var decoder = WindowsAudioDecoder.Open(path);

        // Prime then seek (matches real pump usage).
        var buffer = CreateAnalysisBuffer(decoder);
        Assert.True(decoder.Read(buffer) > 0);

        var target = FixtureHelper.SegmentCenter(segmentIndex, SegmentMs);
        decoder.Seek(target);

        var read = ReadAnalysisWindow(decoder, buffer);
        Assert.True(read > 0, $"No samples after seek to segment {segmentIndex}.");
        PcmFrequencyAnalyzer.AssertSegmentFrequency(
            buffer.AsSpan(0, read),
            decoder.Format.SampleRate,
            decoder.Format.ChannelCount,
            expectedHz);
    }

    [Fact]
    public void Flac_RepeatedForwardAndBackwardSeeks()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(SegmentMs);
        using var decoder = WindowsAudioDecoder.Open(path);
        var buffer = CreateAnalysisBuffer(decoder);

        var sequence = new (int Index, float Hz)[]
        {
            (0, 220f),
            (2, 880f),
            (1, 440f),
            (3, 1760f),
            (0, 220f),
            (3, 1760f),
        };

        foreach (var (index, hz) in sequence)
        {
            decoder.Seek(FixtureHelper.SegmentCenter(index, SegmentMs));
            var read = ReadAnalysisWindow(decoder, buffer);
            Assert.True(read > 0);
            PcmFrequencyAnalyzer.AssertSegmentFrequency(
                buffer.AsSpan(0, read),
                decoder.Format.SampleRate,
                decoder.Format.ChannelCount,
                hz);
        }
    }

    [Fact]
    public void Flac_SeekToBeginAndNearEnd()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(SegmentMs);
        using var decoder = WindowsAudioDecoder.Open(path);
        var buffer = CreateAnalysisBuffer(decoder);

        decoder.Seek(TimeSpan.Zero);
        var read = ReadAnalysisWindow(decoder, buffer);
        Assert.True(read > 0);
        PcmFrequencyAnalyzer.AssertSegmentFrequency(
            buffer.AsSpan(0, read),
            decoder.Format.SampleRate,
            decoder.Format.ChannelCount,
            220f);

        var nearEnd = decoder.Duration - TimeSpan.FromMilliseconds(80);
        if (nearEnd < TimeSpan.Zero)
        {
            nearEnd = FixtureHelper.SegmentCenter(3, SegmentMs);
        }

        decoder.Seek(nearEnd);
        read = ReadAnalysisWindow(decoder, buffer);
        Assert.True(read > 0);
        PcmFrequencyAnalyzer.AssertSegmentFrequency(
            buffer.AsSpan(0, read),
            decoder.Format.SampleRate,
            decoder.Format.ChannelCount,
            1760f);
    }

    [Fact]
    public void Flac_SeekAfterFullDrain_UsesFallbackIfNeeded()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(SegmentMs);
        using var decoder = WindowsAudioDecoder.Open(path);
        var buffer = CreateAnalysisBuffer(decoder);
        while (decoder.Read(buffer) > 0)
        {
        }

        decoder.Seek(FixtureHelper.SegmentCenter(1, SegmentMs));
        var read = ReadAnalysisWindow(decoder, buffer);
        Assert.True(read > 0);
        PcmFrequencyAnalyzer.AssertSegmentFrequency(
            buffer.AsSpan(0, read),
            decoder.Format.SampleRate,
            decoder.Format.ChannelCount,
            440f);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Flac_Player_SeekWhilePlayingPausedStopped()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(SegmentMs);
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);

        Assert.True(player.Duration > TimeSpan.FromMilliseconds(SegmentMs * 3));

        // Stopped seek
        player.Seek(FixtureHelper.SegmentCenter(2, SegmentMs));
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.True(player.Position >= TimeSpan.FromMilliseconds(SegmentMs * 2 - 50));

        player.Play();
        await Task.Delay(50);
        player.Seek(FixtureHelper.SegmentCenter(0, SegmentMs));
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.True(player.Position < TimeSpan.FromMilliseconds(SegmentMs + 50));

        player.Pause();
        player.Seek(FixtureHelper.SegmentCenter(3, SegmentMs));
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.True(player.Position >= TimeSpan.FromMilliseconds(SegmentMs * 3 - 50));

        player.Stop();
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Flac_Player_RapidSeeks_LastWins()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(SegmentMs);
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);
        player.Play();

        for (var i = 0; i < 12; i++)
        {
            player.Seek(FixtureHelper.SegmentCenter(i % 4, SegmentMs));
        }

        var expected = FixtureHelper.SegmentCenter(3, SegmentMs);
        Assert.InRange(player.Position.TotalMilliseconds, expected.TotalMilliseconds - 80, expected.TotalMilliseconds + 120);
        player.Stop();
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Flac_Player_NaturalCompletion()
    {
        // Short single-tone FLAC for timely completion.
        var path = FixtureHelper.EnsureToneFlac(TimeSpan.FromMilliseconds(220));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync(path);
        player.Volume = 0.2f;
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref completed) == 0 && sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(40);
        }

        Assert.Equal(1, Volatile.Read(ref completed));
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Flac_Player_SeekNearEnd_WhilePaused_DoesNotComplete()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(SegmentMs);
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync(path);
        player.Play();
        await Task.Delay(40);
        player.Pause();
        player.Seek(player.Duration - TimeSpan.FromMilliseconds(40));
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.Equal(PlaybackState.Paused, player.State);
        player.Stop();
        Assert.Equal(0, Volatile.Read(ref completed));
    }

    [Fact]
    public void Flac_InvalidFile_ThrowsMappedException()
    {
        var path = Path.Combine(FixtureHelper.FixturesDirectory, "truncated.flac");
        File.WriteAllBytes(path, "not a flac file"u8.ToArray());
        Assert.ThrowsAny<AudioException>(() => WindowsAudioDecoder.Open(path));
    }

    private static float[] CreateAnalysisBuffer(IAudioDecoder decoder)
        => new float[Math.Max(2048, decoder.Format.SampleRate * decoder.Format.ChannelCount / 8)];

    private static int ReadAnalysisWindow(IAudioDecoder decoder, float[] buffer)
    {
        // Pull enough for frequency analysis; allow a couple of reads if the first is short.
        var total = 0;
        for (var i = 0; i < 4 && total < buffer.Length / 2; i++)
        {
            var read = decoder.Read(buffer.AsSpan(total));
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
