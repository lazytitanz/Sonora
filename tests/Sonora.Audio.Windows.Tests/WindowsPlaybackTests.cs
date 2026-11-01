using Sonora.Audio.Formats;
using Sonora.Audio.Windows;

namespace Sonora.Audio.Windows.Tests;

public sealed class WindowsPlaybackTests
{
    [Fact]
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

    [Fact]
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

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task M4a_SeekDuringPlayback()
    {
        var path = FixtureHelper.EnsureToneM4a(TimeSpan.FromMilliseconds(800));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);

        Assert.True(player.Duration > TimeSpan.FromMilliseconds(500));

        player.Play();
        await Task.Delay(80);

        player.Seek(TimeSpan.FromMilliseconds(400));
        Assert.True(player.Position >= TimeSpan.FromMilliseconds(350), $"Position after seek was {player.Position}.");
        Assert.True(player.BufferedPosition >= player.Position - TimeSpan.FromMilliseconds(150));

        player.Stop();
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Wav_SeekAndPositionsAdvance()
    {
        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(800));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);

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

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Hardware_PlayAudibleSmoke()
    {
        if (!ShouldRunHardwareTests())
        {
            return;
        }

        var path = FixtureHelper.EnsureToneWav(TimeSpan.FromMilliseconds(400));
        using var engine = SonoraWindows.CreateEngine();
        using var player = engine.CreatePlayer();
        await player.LoadAsync(path);
        player.Volume = 0.1f;
        player.Play();
        await Task.Delay(450);
        player.Stop();
    }

    private static bool ShouldRunHardwareTests()
        => string.Equals(Environment.GetEnvironmentVariable("SONORA_RUN_HARDWARE_TESTS"), "1", StringComparison.Ordinal);
}
