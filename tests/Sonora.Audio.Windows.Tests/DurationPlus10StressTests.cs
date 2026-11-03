using System.Diagnostics;
using Sonora.Audio;
using Sonora.Audio.Diagnostics;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Windows.Decoding;
using Sonora.Audio.Windows.DSP;
using Sonora.Audio.Windows.Tests.Fakes;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Windows-local stress using real Media Foundation decoding (MP3/M4A) and a pulling fake output.
/// Does not require hardware speakers. Complements fake-decoder race tests for Duration+10 hangs
/// that may involve native decoder Seek.
/// </summary>
public sealed class DurationPlus10StressTests
{
    [Fact]
    public async Task RapidPastEndSeeks_Mp3ThenM4a_Gapless_DoesNotStallPlaying()
    {
        var a = FixtureHelper.EnsureToneMp3(TimeSpan.FromSeconds(2));
        var b = FixtureHelper.EnsureToneM4a(TimeSpan.FromSeconds(2));

        using var engine = new AudioEngine(
            new WindowsAudioDecoderFactory(),
            new PullingFakeOutputFactory(),
            new AudioEngineOptions
            {
                PrimeDuration = TimeSpan.FromMilliseconds(20),
                BufferDuration = TimeSpan.FromMilliseconds(100),
                EffectFactory = new WindowsAudioEffectFactory(),
                FormatConverter = new WindowsAudioFormatConverter(),
            });
        using var player = engine.CreatePlayer();

        var transitions = 0;
        player.TrackTransitioned += (_, _) => Interlocked.Increment(ref transitions);

        await player.LoadAsync(a);
        await player.PreloadNextAsync(b);
        Assert.Equal(NextTrackState.Ready, player.NextTrackState);
        player.Play();

        // Rapid Duration+10 style seeks (clamped to end) then follow-ups on the next track.
        for (var i = 0; i < 12; i++)
        {
            player.SetNextSeekCorrelationId(1000 + i);
            try
            {
                player.Seek(player.Duration + TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException)
            {
                // Superseded / no source during transition — acceptable under rapid fire.
            }

            await Task.Delay(15);
        }

        // Allow promote / settle.
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref transitions) < 1 && sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(20);
        }

        // Additional mid-track seeks on whatever is active.
        for (var i = 0; i < 8; i++)
        {
            player.SetNextSeekCorrelationId(2000 + i);
            var dur = player.Duration;
            var target = dur > TimeSpan.Zero
                ? TimeSpan.FromTicks(dur.Ticks / 3)
                : TimeSpan.FromMilliseconds(50);
            try
            {
                _ = player.SeekWithResult(target);
            }
            catch (InvalidOperationException)
            {
                // superseded
            }
        }

        await Task.Delay(200);

        var diag = player.GetDiagnostics();
        Assert.Equal(0, diag.SeekInProgress);
        Assert.True(diag.EffectsBarrier == 0, "Effects barrier left armed after seeks.");
        Assert.NotEqual(SeekSilenceReason.EffectsBarrier, diag.LastSilenceReason);

        // Must not be permanently Playing with frozen consume and no callback progress.
        if (diag.State == PlaybackState.Playing)
        {
            var consumed0 = diag.SamplesConsumed;
            var callbacks0 = diag.CallbackCount;
            await Task.Delay(300);
            diag = player.GetDiagnostics();
            var progressed = diag.SamplesConsumed > consumed0 || diag.CallbackCount > callbacks0;
            Assert.True(
                progressed || diag.LastSilenceReason is SeekSilenceReason.EndOfStream or SeekSilenceReason.PendingNext,
                $"Playing without progress: consumed {consumed0}->{diag.SamplesConsumed}, callbacks {callbacks0}->{diag.CallbackCount}, silence={diag.LastSilenceReason}, stopPumpTimeouts={diag.StopPumpTimeoutCount}");
        }

        Assert.True(transitions >= 1, "Expected at least one gapless transition after past-end seeks.");
    }
}
