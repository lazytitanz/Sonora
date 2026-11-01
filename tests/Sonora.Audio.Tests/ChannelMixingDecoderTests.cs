using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public sealed class ChannelMixingDecoderTests
{
    [Fact]
    public void MonoToStereo_DuplicatesChannel()
    {
        var mono = AudioFormat.CreateFloat32(44100, 1);
        var stereo = AudioFormat.CreateFloat32(44100, 2);
        var inner = new SequenceFakeDecoder(mono, new float[] { 0.5f, -0.25f });
        using var mixer = new ChannelMixingDecoder(inner, stereo);

        var buffer = new float[4];
        Assert.Equal(4, mixer.Read(buffer));
        Assert.Equal(0.5f, buffer[0]);
        Assert.Equal(0.5f, buffer[1]);
        Assert.Equal(-0.25f, buffer[2]);
        Assert.Equal(-0.25f, buffer[3]);
    }

    [Fact]
    public void StereoToMono_AveragesChannels()
    {
        var stereo = AudioFormat.CreateFloat32(44100, 2);
        var mono = AudioFormat.CreateFloat32(44100, 1);
        var inner = new SequenceFakeDecoder(stereo, new float[] { 1f, 0f, 0.5f, 0.5f });
        using var mixer = new ChannelMixingDecoder(inner, mono);

        var buffer = new float[2];
        Assert.Equal(2, mixer.Read(buffer));
        Assert.Equal(0.5f, buffer[0]);
        Assert.Equal(0.5f, buffer[1]);
    }

    [Fact]
    public void Passthrough_WhenFormatsMatch_ReturnsSameInstance()
    {
        var format = AudioFormat.CreateFloat32(44100, 2);
        var inner = FakeAudioDecoder.CreateSilence(format, TimeSpan.FromMilliseconds(10));
        var converter = new PassthroughFormatConverter();
        Assert.Same(inner, converter.Convert(inner, format));
    }

    [Fact]
    public void Passthrough_RateMismatch_Throws()
    {
        var source = FakeAudioDecoder.CreateSilence(AudioFormat.CreateFloat32(44100, 2), TimeSpan.FromMilliseconds(10));
        var converter = new PassthroughFormatConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.Convert(source, AudioFormat.CreateFloat32(48000, 2)));
    }
}
