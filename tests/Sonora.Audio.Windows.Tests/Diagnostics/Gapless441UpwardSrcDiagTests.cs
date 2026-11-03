using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Windows.Decoding;

namespace Sonora.Audio.Windows.Tests.Diagnostics;

/// <summary>
/// Optional real-fixture probes for 44.1 kHz upward SRC during gapless investigation.
/// Skips when local Downloads fixtures are absent. Does not change production code.
/// </summary>
public sealed class Gapless441UpwardSrcDiagTests
{
    private static readonly string Downloads = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");

    public static TheoryData<string, int> UpwardTargets { get; } = new()
    {
        { "test-audio-ogg-vorbis-q5.ogg", 48000 },
        { "test-audio-ogg-vorbis-q5.ogg", 96000 },
        { "test-audio-aac-256kbps.m4a", 48000 },
        { "test-audio-aac-256kbps.m4a", 96000 },
    };

    [Theory]
    [MemberData(nameof(UpwardTargets))]
    public async Task RealFixture_UpwardConvert_OfflineDrain_NoInvalidSamples(string fileName, int targetRate)
    {
        var path = Path.Combine(Downloads, fileName);
        if (!File.Exists(path))
        {
            return; // local listening fixtures only
        }

        var factory = new WindowsAudioDecoderFactory();
        using var native = await factory.CreateAsync(path);
        Assert.Equal(44100, native.Format.SampleRate);

        // Characterize mid-stream short reads on the native decoder (Probe C risk).
        var shortStats = MeasureShortReads(native, requestFloats: 8192, maxFramesToRead: native.Format.SampleRate * 2);
        native.Seek(TimeSpan.Zero);

        var converter = new WindowsAudioFormatConverter();
        using var converted = converter.Convert(
            await factory.CreateAsync(path),
            AudioFormat.CreateFloat32(targetRate, native.Format.ChannelCount));

        Assert.Equal(targetRate, converted.Format.SampleRate);

        var pcm = Drain(converted, chunkFloats: 4096, maxSamples: targetRate * converted.Format.ChannelCount * 3);
        var amp = PcmIntegrityMetrics.Analyze(pcm);

        Assert.Equal(0, amp.NonFiniteCount);
        Assert.True(amp.Peak > 1e-4f, "Converted fixture was silent.");
        Assert.True(
            amp.Peak < 4f,
            $"Converted peak {amp.Peak} looks pathological for {fileName} → {targetRate} "
            + $"(nativeShortReads={shortStats.PositiveShortReads}/{shortStats.TotalReads}).");

        Assert.True(shortStats.TotalReads > 0, "Native decoder produced no reads.");
    }

    [Fact]
    public async Task RealFixture_NativeDecoder_FillsLargeRequests_NoMidStreamShorts()
    {
        // Regression: WaveStreamAudioDecoder previously capped every Read at 1024 floats, which
        // made WDL flush on every DecodePump iteration during 44.1→48/96 gapless preload.
        foreach (var name in new[]
                 {
                     "test-audio-ogg-vorbis-q5.ogg",
                     "test-audio-aac-256kbps.m4a",
                     "test-audio-opus-128kbps.ogg",
                     "test-audio-wav-16bit-44khz.wav",
                 })
        {
            var path = Path.Combine(Downloads, name);
            if (!File.Exists(path))
            {
                continue;
            }

            var factory = new WindowsAudioDecoderFactory();
            using var decoder = await factory.CreateAsync(path);
            var stats = MeasureShortReads(decoder, requestFloats: 8192, maxFramesToRead: decoder.Format.SampleRate * 2);
            Assert.True(
                stats.MidStreamPositiveShortReads == 0,
                $"{name}: mid-stream positive short reads={stats.MidStreamPositiveShortReads} "
                + $"(totalReads={stats.TotalReads}, eosShort={stats.EosPositiveShortRead}). "
                + "Mid-stream shorts flush NAudio 3.3 WDL on every under-fill.");
            Assert.True(
                stats.MaxRead >= 8192 || stats.TotalReads <= 1,
                $"{name}: expected full 8192 fills before EOS; maxRead={stats.MaxRead}");
        }
    }

    private static ShortReadStats MeasureShortReads(IAudioDecoder decoder, int requestFloats, int maxFramesToRead)
    {
        var channels = Math.Max(1, decoder.Format.ChannelCount);
        var buffer = new float[requestFloats];
        var totalReads = 0;
        var positiveShort = 0;
        var midStreamShort = 0;
        var frames = 0;
        var lastWasShort = false;
        var maxRead = 0;
        int read;
        while (frames < maxFramesToRead && (read = decoder.Read(buffer)) > 0)
        {
            totalReads++;
            maxRead = Math.Max(maxRead, read);
            var requested = buffer.Length;
            requested -= requested % channels;
            if (read < requested)
            {
                positiveShort++;
                lastWasShort = true;
                midStreamShort++;
            }
            else
            {
                lastWasShort = false;
            }

            frames += read / channels;
        }

        var next = decoder.Read(buffer);
        var eosShort = lastWasShort && next <= 0;
        if (eosShort && midStreamShort > 0)
        {
            midStreamShort--;
        }

        return new ShortReadStats(totalReads, positiveShort, midStreamShort, eosShort, maxRead);
    }

    private static float[] Drain(IAudioDecoder decoder, int chunkFloats, int maxSamples)
    {
        var list = new List<float>(Math.Min(maxSamples, chunkFloats * 8));
        var buffer = new float[chunkFloats];
        int read;
        while (list.Count < maxSamples && (read = decoder.Read(buffer)) > 0)
        {
            var take = Math.Min(read, maxSamples - list.Count);
            for (var i = 0; i < take; i++)
            {
                list.Add(buffer[i]);
            }
        }

        return list.ToArray();
    }

    private readonly record struct ShortReadStats(
        int TotalReads,
        int PositiveShortReads,
        int MidStreamPositiveShortReads,
        bool EosPositiveShortRead,
        int MaxRead);
}
