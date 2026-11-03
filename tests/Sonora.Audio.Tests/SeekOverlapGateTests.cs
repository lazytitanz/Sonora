using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;
using Xunit.Abstractions;

namespace Sonora.Audio.Tests;

/// <summary>
/// Verifies whether <see cref="AudioPlayer.SeekWithResult"/> bodies can overlap.
/// The boolean effects-barrier hypothesis is only viable if overlapping seeks are possible.
/// </summary>
public sealed class SeekOverlapGateTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);
    private readonly ITestOutputHelper _output;

    public SeekOverlapGateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SeekWithResult_Bodies_Serialize_On_DecoderSeekGate()
    {
        // Two concurrent SeekWithResult calls: the second must wait on _decoderSeekGate
        // (and/or command path) so SeekOverlapEnter either never fires with bodies>1 during
        // Decoder.Seek, or both complete without leaving seekInProgress stuck.
        var hold = new HoldableFakeDecoder(FakeAudioDecoder.CreateTone(Format, TimeSpan.FromSeconds(3)));
        hold.Release();
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(hold),
            new FakeAudioOutputFactory(),
            new AudioEngineOptions { PrimeDuration = TimeSpan.Zero });
        using var player = engine.CreatePlayer();
        await player.LoadAsync("a.wav");
        player.Play();

        hold.HoldSeek();
        Exception? firstError = null;
        Exception? secondError = null;
        var first = new Thread(() =>
        {
            try
            {
                player.Seek(TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex)
            {
                firstError = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Sonora.Test.SeekOverlap.A",
        };
        first.Start();
        Assert.True(hold.WaitUntilSeekHeld(TimeSpan.FromSeconds(5)));

        var secondStarted = new ManualResetEventSlim(false);
        var second = new Thread(() =>
        {
            secondStarted.Set();
            try
            {
                player.Seek(TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                secondError = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Sonora.Test.SeekOverlap.B",
        };
        second.Start();
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(2)));

        // While first holds Decoder.Seek, second may be inside SeekWithResult (overlap) or blocked
        // before Decoder.Seek. Record evidence via the lifecycle trace.
        await Task.Delay(50);
        var buffer = new Diagnostics.SeekLifecycleEvent[256];
        var n = player.CopySeekLifecycleTrace(buffer, out _, out _);
        var overlapEnters = 0;
        var maxBodiesFlag = 0;
        for (var i = 0; i < n; i++)
        {
            if (buffer[i].Stage == Diagnostics.SeekLifecycleStage.SeekOverlapEnter)
            {
                overlapEnters++;
                // SeekOverlapEnter records bodies-in-flight in ExtraFlags via RecordSeekTrace(extraFlags: bodies).
                if (buffer[i].Flags > maxBodiesFlag)
                {
                    maxBodiesFlag = buffer[i].Flags;
                }
            }
        }

        hold.ReleaseSeek();
        Assert.True(first.Join(TimeSpan.FromSeconds(5)));
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));

        var diag = player.GetDiagnostics();
        Assert.Equal(0, diag.SeekInProgress);

        _output.WriteLine(
            "OVERLAP_FINDING: SeekOverlapEnter={0}, maxFlags={1}, firstError={2}, secondError={3}",
            overlapEnters,
            maxBodiesFlag,
            firstError?.GetType().Name ?? "none",
            secondError?.GetType().Name ?? "none");

        if (overlapEnters > 0)
        {
            _output.WriteLine(
                "BOOLEAN_BARRIER_HYPOTHESIS: still viable at engine layer (nested SeekWithResult bodies observed).");
        }
        else
        {
            _output.WriteLine(
                "BOOLEAN_BARRIER_HYPOTHESIS: demote unless TestPlayer bypasses gates — bodies did not nest during Decoder.Seek hold.");
        }
    }
}
