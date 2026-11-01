# Getting started with Sonora

## Prerequisites

- .NET 10 SDK
- Windows 10/11 for playback (`Sonora.Audio.Windows`)

## Create an engine and player

```csharp
using Sonora.Audio;
using Sonora.Audio.Windows;

using var engine = SonoraWindows.CreateEngine();
using var player = engine.CreatePlayer();
```

## Load and play

```csharp
await player.LoadAsync(@"C:\music\track.mp3");

player.Volume = 0.8f;
player.PlaybackCompleted += (_, _) => Console.WriteLine("done");
player.ErrorOccurred += (_, ex) => Console.WriteLine(ex.Message);

player.Play();
```

## Output devices

```csharp
using Sonora.Audio.Devices;

foreach (var device in engine.OutputDevices.GetDevices())
{
    Console.WriteLine($"{device.Name} ({device.State}) default={device.IsDefault}");
}

var headphones = engine.OutputDevices.GetDevices()
    .FirstOrDefault(d => d.State == AudioDeviceState.Active && d.Name.Contains("Headphone"));

if (headphones is not null)
{
    await player.SetOutputDeviceAsync(headphones.Id);
}

// null = follow the Windows Multimedia default render endpoint
await player.SetOutputDeviceAsync(null);

player.OutputDeviceChanged += (_, e) =>
    Console.WriteLine($"{e.Reason}: preferred={e.PreferredDeviceId} active={e.ActiveDeviceId} status={e.Status}");
```

Device switches reinitialize WASAPI and may briefly interrupt audio. Volume, DSP settings, and a ready next track are preserved. Prefer `Stop` when you do not want auto-resume after a device is restored.

## Gapless next track

```csharp
await player.LoadAsync(@"C:\music\Track01.flac");
await player.PreloadNextAsync(@"C:\music\Track02.flac");

player.TrackTransitioned += (_, e) =>
    Console.WriteLine($"Now playing {e.NextPath}");

player.Play();
// Inspect player.NextTrackState / NextTrackPath; call ClearNext() to drop the lookahead.
```

When the current track drains and the next track is `Ready`, Sonora transitions without stopping WASAPI and without raising `PlaybackCompleted` between tracks. If nothing is ready, `PlaybackCompleted` fires as usual.

## Control playback

```csharp
player.Pause();
player.Play();                         // resume
player.Seek(TimeSpan.FromMinutes(1));  // preserves a safe preloaded next track
player.Stop();                         // resets to 00:00; clears next; does not raise PlaybackCompleted
```

## Read timing information

```csharp
Console.WriteLine(player.State);
Console.WriteLine(player.SourcePath);
Console.WriteLine(player.Duration);
Console.WriteLine(player.Position);           // estimated audible (active track)
Console.WriteLine(player.BufferedPosition);   // buffered decode frontier
```

`Position` is an estimate. Gapless handoffs update identity at the output-consumption boundary and are not ear-sample-accurate. See the README for accuracy caveats.

## Console sample

```bash
dotnet run --project samples/Sonora.ConsolePlayer -- "C:\music\track.wav"
```

Keys: Space play/pause, S stop, arrows seek, +/- volume, Q quit.

## Equalizer and preamp

```csharp
using Sonora.Audio.DSP;

var eq = player.Effects.AddEqualizer(); // six-band default
eq.SetBandGain(1, 4f);
eq.ApplyPreset(EqualizerPresets.BassBoost);

player.Effects.AddPreamp(-2f);   // dB; separate from player.Volume
player.Effects.AddLimiter();     // optional clipping protection
```

Effects process after the PCM buffer so slider changes are audible immediately. Use `SonoraWindows.CreateEngine()` so the Windows effect factory is registered.

## Supported formats

On Windows, Sonora supports WAV, AIFF, MP3, ADTS AAC, M4A (AAC and ALAC), WMA, FLAC, Ogg Vorbis, Ogg Opus, and WebM Opus when the host can decode them. See the README format table for backends and caveats.

FLAC / Media Foundation seeking is fixture-verified for correct audio content after seeks, but positioning is not guaranteed to be sample-accurate.
