using Sonora.Audio.Seeking;
using Sonora.Audio.Windows.Decoding;

namespace Sonora.Audio.Windows.Tests;

public sealed class SeekAccuracyTests
{
    [Fact]
    public void Wav_Seek_IsExact_AndMatchesSampleIndex()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(500));
        using var decoder = WindowsAudioDecoder.Open(path);

        var target = TimeSpan.FromMilliseconds(123);
        var result = decoder.Seek(target);

        Assert.Equal(AudioSeekAccuracy.Exact, result.Accuracy);
        Assert.NotNull(result.ActualPosition);
        Assert.Equal(result.RequestedPosition, result.ActualPosition);

        // Sequential reference from a fresh decoder for the same backend.
        using var reference = WindowsAudioDecoder.Open(path);
        var skipFrames = reference.Format.TimeToFrames(result.ActualPosition!.Value);
        DiscardFrames(reference, skipFrames);

        var expected = new float[reference.Format.ChannelCount * 64];
        var actual = new float[decoder.Format.ChannelCount * 64];
        var expectedRead = reference.Read(expected);
        var actualRead = decoder.Read(actual);
        Assert.Equal(expectedRead, actualRead);
        Assert.True(expectedRead > 0);
        Assert.Equal(expected.AsSpan(0, expectedRead).ToArray(), actual.AsSpan(0, actualRead).ToArray());
    }

    [Fact]
    public void Wav_RepeatedForwardAndBackwardSeeks_RemainExact()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(800));
        using var decoder = WindowsAudioDecoder.Open(path);

        foreach (var ms in new[] { 0, 100, 400, 50, 700, 0 })
        {
            var result = decoder.Seek(TimeSpan.FromMilliseconds(ms));
            Assert.Equal(AudioSeekAccuracy.Exact, result.Accuracy);
            Assert.Equal(result.RequestedPosition, result.ActualPosition);
            var buffer = new float[1024];
            if (result.RequestedPosition < decoder.Duration)
            {
                Assert.True(decoder.Read(buffer) > 0);
            }
        }
    }

    [Fact]
    public void Flac_Seek_DoesNotClaimExactFromMediaFoundationTimestamps()
    {
        var path = FixtureHelper.EnsureSegmentedFlac();
        using var decoder = WindowsAudioDecoder.Open(path);

        var result = decoder.Seek(TimeSpan.FromMilliseconds(375));
        Assert.NotEqual(AudioSeekAccuracy.Exact, result.Accuracy);
        if (result.Accuracy == AudioSeekAccuracy.Unknown)
        {
            Assert.Null(result.ActualPosition);
        }
        else
        {
            Assert.NotNull(result.ActualPosition);
        }

        var buffer = new float[4096];
        Assert.True(decoder.Read(buffer) > 0);
    }

    [Fact]
    public void Mp3_Seek_DoesNotClaimExact()
    {
        var path = FixtureHelper.EnsureToneMp3(TimeSpan.FromMilliseconds(400));
        using var decoder = WindowsAudioDecoder.Open(path);
        var result = decoder.Seek(TimeSpan.FromMilliseconds(120));
        Assert.NotEqual(AudioSeekAccuracy.Exact, result.Accuracy);
        Assert.True(decoder.Read(new float[2048]) > 0);
    }

    [Fact]
    public void Flac_SeekAfterFullDrain_RemainsReadableWithoutExactClaim()
    {
        var path = FixtureHelper.EnsureSegmentedFlac();
        using var decoder = WindowsAudioDecoder.Open(path);
        var buffer = new float[4096];
        while (decoder.Read(buffer) > 0)
        {
            // drain
        }

        var result = decoder.Seek(TimeSpan.FromMilliseconds(100));
        Assert.NotEqual(AudioSeekAccuracy.Exact, result.Accuracy);
        Assert.True(decoder.Read(buffer) > 0);
    }

    private static void DiscardFrames(Sonora.Audio.Abstractions.IAudioDecoder decoder, long frames)
    {
        if (frames <= 0)
        {
            return;
        }

        var channels = decoder.Format.ChannelCount;
        var remaining = frames * channels;
        var scratch = new float[Math.Min(4096, (int)Math.Min(remaining, 4096))];
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, scratch.Length);
            chunk -= chunk % channels;
            if (chunk <= 0)
            {
                break;
            }

            var read = decoder.Read(scratch.AsSpan(0, chunk));
            if (read <= 0)
            {
                break;
            }

            remaining -= read - (read % channels);
        }
    }
}
