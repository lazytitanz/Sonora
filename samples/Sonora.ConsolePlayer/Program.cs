using Sonora.Audio;
using Sonora.Audio.Formats;
using Sonora.Audio.Windows;

if (args.Length == 0)
{
    Console.WriteLine("Sonora Console Player");
    Console.WriteLine("Usage: Sonora.ConsolePlayer <audio-file>");
    Console.WriteLine();
    Console.WriteLine("Keys: Space=Play/Pause  S=Stop  Left/Right=Seek  +/-=Volume  Q=Quit");
    return 1;
}

var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 2;
}

using var engine = SonoraWindows.CreateEngine();
using var player = engine.CreatePlayer();

player.PlaybackCompleted += (_, _) =>
{
    Console.WriteLine();
    Console.WriteLine("Playback completed.");
};

player.ErrorOccurred += (_, ex) =>
{
    Console.WriteLine();
    Console.WriteLine($"Error: {ex.Message}");
};

player.Underrun += (_, _) => Console.Write("!");

try
{
    Console.WriteLine($"Loading {path} ...");
    await player.LoadAsync(path);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load audio: {ex.Message}");
    return 3;
}

Console.WriteLine($"Duration: {FormatTime(player.Duration)}");
Console.WriteLine("Keys: Space=Play/Pause  S=Stop  Left/Right=Seek  +/-=Volume  Q=Quit");
Console.WriteLine();

player.Volume = 0.8f;
player.Play();

while (true)
{
    while (!Console.KeyAvailable)
    {
        WriteStatus(player);
        await Task.Delay(100);
        if (player.State == PlaybackState.Stopped && Console.KeyAvailable == false)
        {
            // keep UI updating after natural completion
        }
    }

    var key = Console.ReadKey(intercept: true);
    switch (key.Key)
    {
        case ConsoleKey.Q:
        case ConsoleKey.Escape:
            player.Stop();
            return 0;

        case ConsoleKey.Spacebar:
            if (player.State == PlaybackState.Playing)
            {
                player.Pause();
            }
            else
            {
                player.Play();
            }

            break;

        case ConsoleKey.S:
            player.Stop();
            break;

        case ConsoleKey.LeftArrow:
            SeekBy(player, TimeSpan.FromSeconds(-5));
            break;

        case ConsoleKey.RightArrow:
            SeekBy(player, TimeSpan.FromSeconds(5));
            break;

        case ConsoleKey.OemPlus:
        case ConsoleKey.Add:
            player.Volume = Math.Clamp(player.Volume + 0.05f, 0f, 1f);
            break;

        case ConsoleKey.OemMinus:
        case ConsoleKey.Subtract:
            player.Volume = Math.Clamp(player.Volume - 0.05f, 0f, 1f);
            break;
    }
}

static void SeekBy(AudioPlayer player, TimeSpan delta)
{
    var target = player.Position + delta;
    if (target < TimeSpan.Zero)
    {
        target = TimeSpan.Zero;
    }

    if (player.Duration > TimeSpan.Zero && target > player.Duration)
    {
        target = player.Duration;
    }

    try
    {
        var result = player.SeekWithResult(target);
        if (result.Accuracy != Sonora.Audio.Seeking.AudioSeekAccuracy.Exact)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Seek {result.Accuracy}: requested {FormatTime(result.RequestedPosition)}" +
                (result.ActualPosition is { } actual ? $", actual {FormatTime(actual)}" : ", actual unknown"));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"Seek failed: {ex.Message}");
    }
}

static void WriteStatus(AudioPlayer player)
{
    Console.Write(
        $"\r{player.State,-10}  pos {FormatTime(player.Position)} / {FormatTime(player.Duration)}  buf {FormatTime(player.BufferedPosition)}  vol {player.Volume:0.00}   ");
}

static string FormatTime(TimeSpan value)
{
    if (value < TimeSpan.Zero)
    {
        value = TimeSpan.Zero;
    }

    return value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss\.fff");
}
