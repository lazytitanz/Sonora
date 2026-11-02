using Sonora.Audio.Devices;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;
using Sonora.Audio.Windows;

namespace Sonora.Audio.Windows.Tests;

public sealed class WindowsPlaybackTests
{
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Wav_PlayCompletesNaturally()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(200));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();

        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync(path);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.True(player.Duration > TimeSpan.Zero);

        player.Volume = 0.2f;
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (completed == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, Volatile.Read(ref completed));
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task M4a_PlayCompletesNaturally()
    {
        var path = FixtureHelper.EnsureToneM4a(TimeSpan.FromMilliseconds(250));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();

        var completed = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync(path);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.True(player.Duration > TimeSpan.FromMilliseconds(100), $"Duration was {player.Duration}.");

        player.Volume = 0.2f;
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (completed == 0 && sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, Volatile.Read(ref completed));
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task M4a_SeekDuringPlayback()
    {
        var path = FixtureHelper.EnsureToneM4a(TimeSpan.FromMilliseconds(800));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);

        Assert.True(player.Duration > TimeSpan.FromMilliseconds(500));

        player.Volume = 0.1f;
        player.Play();
        await Task.Delay(80);

        player.Seek(TimeSpan.FromMilliseconds(400));
        Assert.True(player.Position >= TimeSpan.FromMilliseconds(350), $"Position after seek was {player.Position}.");
        Assert.True(player.BufferedPosition >= player.Position - TimeSpan.FromMilliseconds(150));

        player.Stop();
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Wav_SeekAndPositionsAdvance()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(800));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);

        player.Volume = 0.1f;
        player.Play();
        await Task.Delay(100);
        var mid = player.Position;
        Assert.True(mid > TimeSpan.Zero);

        player.Seek(TimeSpan.FromMilliseconds(400));
        Assert.True(player.Position >= TimeSpan.FromMilliseconds(350));
        Assert.True(player.BufferedPosition >= player.Position - TimeSpan.FromMilliseconds(100));

        player.Stop();
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task SharedMode_LoadStopCycle_IsStable()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(300));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();

        for (var i = 0; i < 3; i++)
        {
            await player.LoadAsync(path);
            Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
            player.Volume = 0.05f;
            player.Play();
            await Task.Delay(80);
            player.Stop();
            Assert.Equal(PlaybackState.Stopped, player.State);
            Assert.Equal(TimeSpan.Zero, player.Position);
        }
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Hardware_PlayAudibleSmoke()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(400));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);
        player.Volume = 0.1f;
        player.Play();
        await Task.Delay(450);
        player.Stop();
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Hardware_ExclusiveMode_ConfigureAndReturnToShared()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(300));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.Shared,
        });

        Assert.True(
            player.EffectiveOutputMode is AudioOutputMode.Exclusive or AudioOutputMode.Shared,
            "Exclusive should succeed or fall back to shared on this host.");

        player.Volume = 0.05f;
        player.Play();
        await Task.Delay(150);
        player.Pause();

        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Shared,
        });

        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);
        Assert.Equal(PlaybackState.Paused, player.State);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Hardware_SharedToExclusive_WhilePlaying_FallbackNone()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(1500));
        using var engine = SonoraWindows.CreateEngine();
        await using var player = engine.CreatePlayer();

        Exception? error = null;
        player.ErrorOccurred += (_, ex) => Interlocked.Exchange(ref error, ex);

        await player.LoadAsync(path);
        player.Volume = 0.05f;
        player.Play();
        await Task.Delay(200);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(AudioOutputMode.Shared, player.EffectiveOutputMode);

        try
        {
            await player.ConfigureOutputAsync(new AudioOutputConfiguration
            {
                Mode = AudioOutputMode.Exclusive,
                ExclusiveFallback = ExclusiveModeFallback.None,
            });
        }
        catch (AudioOutputException)
        {
            // Device may reject exclusive; must not crash and must leave a recoverable state.
            Assert.True(player.HasSource);
            Assert.NotEqual(PlaybackState.Playing, player.State); // transport stopped on strict fail
            return;
        }

        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.Equal(PlaybackState.Playing, player.State);

        // Continue for a bit — must not raise AUDCLNT_E_BUFFER_SIZE / recovery storm mid-stream.
        await Task.Delay(400);
        Assert.Null(error);
        Assert.True(
            player.State is PlaybackState.Playing or PlaybackState.Stopped,
            $"Unexpected state after exclusive switch: {player.State}");
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Hardware_Exclusive_NaturalEos_DoesNotRaiseBufferSizeError()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(350));
        using var engine = SonoraWindows.CreateEngine();
        await using var player = engine.CreatePlayer();

        Exception? error = null;
        var completed = 0;
        player.ErrorOccurred += (_, ex) => Interlocked.Exchange(ref error, ex);
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);

        await player.LoadAsync(path);
        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });

        if (player.EffectiveOutputMode != AudioOutputMode.Exclusive)
        {
            // Host rejected exclusive with fallback None — skip EOS assertion.
            return;
        }

        player.Volume = 0.05f;
        player.Play();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref completed) == 0 && sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(40);
        }

        Assert.Equal(1, Volatile.Read(ref completed));
        Assert.Null(error);
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Hardware_Exclusive_Pause_PositionDoesNotAdvance_AndResumeWorks()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(2000));
        using var engine = SonoraWindows.CreateEngine();
        await using var player = engine.CreatePlayer();

        Exception? error = null;
        player.ErrorOccurred += (_, ex) => Interlocked.Exchange(ref error, ex);

        await player.LoadAsync(path);
        await player.ConfigureOutputAsync(new AudioOutputConfiguration
        {
            Mode = AudioOutputMode.Exclusive,
            ExclusiveFallback = ExclusiveModeFallback.None,
        });
        if (player.EffectiveOutputMode != AudioOutputMode.Exclusive)
        {
            return;
        }

        player.Volume = 0.05f;
        player.Play();
        await Task.Delay(250);
        Assert.Equal(PlaybackState.Playing, player.State);

        player.Pause();
        Assert.Equal(PlaybackState.Paused, player.State);
        var pausedAt = player.Position;
        await Task.Delay(500);
        var pausedDelta = (player.Position - pausedAt).Duration();
        Assert.True(
            pausedDelta < TimeSpan.FromMilliseconds(80),
            $"Exclusive pause advanced position by {pausedDelta.TotalMilliseconds:F1} ms (client likely still rendering).");
        Assert.Null(error);

        player.Play();
        await Task.Delay(350);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.True(
            player.Position >= pausedAt,
            $"Resume rewound or stalled position (pausedAt={pausedAt}, now={player.Position}).");
        // Allow a short settle window: exclusive pause restarts the device clock.
        Assert.True(
            player.Position > pausedAt - TimeSpan.FromMilliseconds(5),
            $"Exclusive resume position {player.Position} drifted behind pause hold {pausedAt}.");
        var advanced = player.Position - pausedAt;
        Assert.True(
            advanced > TimeSpan.FromMilliseconds(40),
            $"Exclusive resume did not advance audible position (delta={advanced.TotalMilliseconds:F1} ms).");
        Assert.Null(error);
        player.Stop();
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Hardware_SharedToExclusive_RepeatedSwitches_DoNotNativeCrash()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(1200));
        using var engine = SonoraWindows.CreateEngine();
        await using var player = engine.CreatePlayer();

        Exception? error = null;
        player.ErrorOccurred += (_, ex) => Interlocked.Exchange(ref error, ex);

        await player.LoadAsync(path);
        player.Volume = 0.04f;
        player.Play();

        for (var i = 0; i < 6; i++)
        {
            await player.ConfigureOutputAsync(new AudioOutputConfiguration
            {
                Mode = i % 2 == 0 ? AudioOutputMode.Exclusive : AudioOutputMode.Shared,
                ExclusiveFallback = ExclusiveModeFallback.None,
            });
            await Task.Delay(120);
            Assert.True(player.HasSource);
            Assert.Null(error);
        }

        // Surviving without a process-killing AV is the primary assertion.
        Assert.True(player.State is PlaybackState.Playing or PlaybackState.Stopped or PlaybackState.Paused);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task Hardware_DeviceSwitchThenSharedToExclusive_WhilePlaying_DoNotNativeCrash()
    {
        // Regression: play → SetOutputDevice → Shared→Exclusive crashed some machines with
        // IAudioRenderClient.GetBuffer AV (0xC0000005) on WasapiPlayer.PlayThread.
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(2000));
        using var engine = SonoraWindows.CreateEngine();
        await using var player = engine.CreatePlayer();

        Exception? error = null;
        player.ErrorOccurred += (_, ex) => Interlocked.Exchange(ref error, ex);

        var devices = engine.OutputDevices.GetDevices()
            .Where(d => d.State == AudioDeviceState.Active)
            .Take(2)
            .ToList();

        await player.LoadAsync(path);
        player.Volume = 0.04f;
        player.Play();
        await Task.Delay(150);

        if (devices.Count >= 2)
        {
            var target = devices.FirstOrDefault(d => d.Id != player.ActiveOutputDeviceId) ?? devices[0];
            await player.SetOutputDeviceAsync(target.Id);
            await Task.Delay(100);
        }
        else
        {
            // Single-device machines: still exercise a no-op preference round-trip.
            await player.SetOutputDeviceAsync(devices[0].Id);
            await Task.Delay(50);
        }

        Assert.Equal(PlaybackState.Playing, player.State);

        try
        {
            await player.ConfigureOutputAsync(new AudioOutputConfiguration
            {
                Mode = AudioOutputMode.Exclusive,
                ExclusiveFallback = ExclusiveModeFallback.None,
            });
        }
        catch (AudioOutputException)
        {
            Assert.True(player.HasSource);
            Assert.Null(error);
            return;
        }

        await Task.Delay(400);
        Assert.Null(error);
        Assert.Equal(AudioOutputMode.Exclusive, player.EffectiveOutputMode);
        Assert.True(player.HasSource);
        Assert.True(player.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Stopped);
    }
}
