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

## Control playback

```csharp
player.Pause();
player.Play();                         // resume
player.Seek(TimeSpan.FromMinutes(1));
player.Stop();                         // resets to 00:00; does not raise PlaybackCompleted
```

## Read timing information

```csharp
Console.WriteLine(player.State);
Console.WriteLine(player.Duration);
Console.WriteLine(player.Position);           // estimated audible
Console.WriteLine(player.BufferedPosition);   // buffered decode frontier
```

`Position` is an estimate. See the README for accuracy caveats.

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

On Windows, Sonora supports WAV, AIFF, MP3, ADTS AAC, M4A (AAC and ALAC), WMA, FLAC, Ogg Vorbis, Ogg Opus, and WebM audio (Opus/Vorbis) when the host can decode them. See the README format table for backends and caveats.

FLAC / Media Foundation seeking is fixture-verified for correct audio content after seeks, but positioning is not guaranteed to be sample-accurate.
