using NAudio.Wave;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Focused Media Foundation probes used to choose the FLAC seek strategy.
/// </summary>
public sealed class MediaFoundationFlacSeekProbeTests
{
    public readonly record struct SeekProbeResult(
        string Method,
        bool Success,
        int SamplesRead,
        float EstimatedHz,
        double ReportedMs,
        string? Error);

    [Fact]
    public void Probe_ImmediateReposition_CurrentTime_And_Position()
    {
        const int segmentMs = 250;
        var path = FixtureHelper.EnsureSegmentedFlac(segmentMs);
        var target = FixtureHelper.SegmentCenter(1, segmentMs); // 375ms → 440 Hz

        var currentTime = ProbeSeek(path, target, expectedHz: 440f, usePosition: false, recreateProvider: true);
        var position = ProbeSeek(path, target, expectedHz: 440f, usePosition: true, recreateProvider: true);
        var currentTimeKeepProvider = ProbeSeek(path, target, expectedHz: 440f, usePosition: false, recreateProvider: false);

        var summary = string.Join(Environment.NewLine, [currentTime.ToString(), position.ToString(), "keepProvider:" + currentTimeKeepProvider]);
        File.WriteAllText(Path.Combine(FixtureHelper.FixturesDirectory, "flac-seek-strategy.marker"), summary);

        Assert.True(
            currentTime.Success || position.Success,
            "Neither CurrentTime nor Position (with provider recreate) produced 440 Hz. " + summary);

        var winner = currentTime.Success && position.Success ? "both"
            : currentTime.Success ? "CurrentTime"
            : "Position";
        File.AppendAllText(
            Path.Combine(FixtureHelper.FixturesDirectory, "flac-seek-strategy.marker"),
            Environment.NewLine + "winner=" + winner + "; keepProviderSuccess=" + currentTimeKeepProvider.Success);
    }

    [Fact]
    public void Probe_AllSegmentCenters_WithWinningStrategy()
    {
        const int segmentMs = 250;
        var path = FixtureHelper.EnsureSegmentedFlac(segmentMs);

        for (var i = 0; i < FixtureHelper.SegmentFrequenciesHz.Length; i++)
        {
            var expected = FixtureHelper.SegmentFrequenciesHz[i];
            var target = FixtureHelper.SegmentCenter(i, segmentMs);
            var result = ProbeSeek(path, target, expected, usePosition: false, recreateProvider: true);
            Assert.True(result.Success, $"Segment {i} ({expected} Hz) failed: {result}");
        }
    }

    [Fact]
    public void Probe_DeferredReposition_AfterPrime()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(segmentMs: 250);
        using var reader = CreateReader(path, repositionInRead: true, requestFloat: true);
        var provider = reader.ToSampleProvider();
        var buffer = new float[4096];
        Assert.True(provider.Read(buffer) > 0);

        reader.CurrentTime = FixtureHelper.SegmentCenter(1);
        Exception? error = null;
        var read = 0;
        try
        {
            // Recreate provider after deferred seek flag is set.
            provider = reader.ToSampleProvider();
            read = provider.Read(buffer);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        File.WriteAllText(
            Path.Combine(FixtureHelper.FixturesDirectory, "flac-deferred-reposition.marker"),
            $"err={error?.GetType().Name}:{error?.Message}; read={read}; t={reader.CurrentTime.TotalMilliseconds:0}");
    }

    [Fact]
    public void Probe_SeekAfterEof_UsesRecreateFallbackOnlyWhenNeeded()
    {
        var path = FixtureHelper.EnsureSegmentedFlac(segmentMs: 200);
        using var reader = CreateReader(path, repositionInRead: false, requestFloat: true);
        var provider = reader.ToSampleProvider();
        var buffer = new float[8192];
        while (provider.Read(buffer) > 0)
        {
        }

        var directOk = false;
        try
        {
            reader.CurrentTime = TimeSpan.FromMilliseconds(100);
            provider = reader.ToSampleProvider();
            directOk = provider.Read(buffer) > 0;
        }
        catch
        {
            directOk = false;
        }

        if (directOk)
        {
            File.WriteAllText(Path.Combine(FixtureHelper.FixturesDirectory, "flac-eof-seek.marker"), "direct-ok");
            return;
        }

        using var recreated = CreateReader(path, repositionInRead: false, requestFloat: true);
        var reprovider = recreated.ToSampleProvider();
        recreated.CurrentTime = TimeSpan.FromMilliseconds(100);
        Assert.True(reprovider.Read(buffer) > 0, "Recreate fallback must restore seekability after EOF.");
        File.WriteAllText(Path.Combine(FixtureHelper.FixturesDirectory, "flac-eof-seek.marker"), "recreate-fallback");
    }

    private static SeekProbeResult ProbeSeek(
        string path,
        TimeSpan target,
        float expectedHz,
        bool usePosition,
        bool recreateProvider)
    {
        try
        {
            using var reader = CreateReader(path, repositionInRead: false, requestFloat: true);
            var provider = reader.ToSampleProvider();
            // Analyze ~120ms after seek.
            var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 8];
            if (provider.Read(buffer) <= 0)
            {
                return new SeekProbeResult(usePosition ? "Position" : "CurrentTime", false, 0, 0, reader.CurrentTime.TotalMilliseconds, "prime-failed");
            }

            if (usePosition)
            {
                reader.Position = TimeToBlockAlignedBytes(reader, target);
            }
            else
            {
                reader.CurrentTime = target;
            }

            if (recreateProvider)
            {
                provider = reader.ToSampleProvider();
            }

            var read = provider.Read(buffer);
            if (read <= 0)
            {
                return new SeekProbeResult(usePosition ? "Position" : "CurrentTime", false, 0, 0, reader.CurrentTime.TotalMilliseconds, "empty-read");
            }

            var freq = PcmFrequencyAnalyzer.EstimateDominantFrequency(
                buffer.AsSpan(0, read),
                reader.WaveFormat.SampleRate,
                reader.WaveFormat.Channels);
            var ok = Math.Abs(freq - expectedHz) <= 40f;
            return new SeekProbeResult(
                usePosition ? "Position" : "CurrentTime",
                ok,
                read,
                freq,
                reader.CurrentTime.TotalMilliseconds,
                null);
        }
        catch (Exception ex)
        {
            return new SeekProbeResult(usePosition ? "Position" : "CurrentTime", false, 0, 0, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal static MediaFoundationReader CreateReader(string path, bool repositionInRead, bool requestFloat)
        => new(path, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RepositionInRead = repositionInRead,
            RequestFloatOutput = requestFloat,
        });

    internal static long TimeToBlockAlignedBytes(WaveStream reader, TimeSpan time)
    {
        var format = reader.WaveFormat;
        var raw = (long)(time.TotalSeconds * format.AverageBytesPerSecond);
        if (format.BlockAlign > 0)
        {
            raw -= raw % format.BlockAlign;
        }

        raw = Math.Clamp(raw, 0, reader.Length > 0 ? reader.Length : long.MaxValue);
        return raw;
    }
}
