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

## Load from HTTP / HTTPS

```csharp
using Sonora.Audio.Sources;

await player.LoadAsync(new HttpAudioSource(new Uri("https://example.com/track.mp3")));

player.NetworkStatusChanged += (_, _) =>
{
    Console.WriteLine($"{player.NetworkTransportStatus} ({player.NetworkBytesBuffered} bytes)");
};

player.Play();

// Finite file vs unknown-length / live:
if (!player.HasKnownDuration)
{
    Console.WriteLine("Duration unknown — do not treat Duration == 0 as empty.");
}
```

String paths stay file-only. Pass `HttpAudioSource` explicitly for URLs. Optional engine-wide defaults live on `AudioEngineOptions.HttpStreaming`.

Seeking works only when the server supports verified byte ranges **and** the decoder can map time to a stream position. Non-seekable sources throw `NotSupportedException` from `Seek` / `SeekWithResult`. Transport failures raise `ErrorOccurred` with `NetworkAudioException` and never `PlaybackCompleted`.

## Read metadata without playback

Metadata extraction does not create a player or initialize WASAPI.

```csharp
using Sonora.Audio.Metadata;
using Sonora.Audio.Windows;

var reader = SonoraWindows.CreateMetadataReader();
var meta = reader.Read(@"C:\music\track.flac");

Console.WriteLine(meta.Title);
Console.WriteLine(string.Join(", ", meta.Artists));
Console.WriteLine(meta.Duration);

var art = reader.Read(
    @"C:\music\track.flac",
    new AudioMetadataReadOptions { IncludeArtwork = true });
```

Unavailable fields are `null` (or empty lists). Titles are never invented from file names. Prefer `IncludeArtwork = false` (default) for large library scans.

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
var seek = player.SeekWithResult(TimeSpan.FromSeconds(30));
// seek.RequestedPosition / seek.ActualPosition / seek.Accuracy
player.Stop();                         // resets to 00:00; clears next; does not raise PlaybackCompleted
```

Seek and Stop’s decoder rewind release the command gate around pump join / `IAudioDecoder.Seek`, so other commands are not frozen during a slow Media Foundation seek. Seek waits for the previous decode worker to exit before touching the decoder; the calling thread can still block for that join or the seek itself — from a UI thread, run seek work asynchronously (as the Test Player bridge does).

## Exclusive output (optional)

Shared mode is the default. To request exclusive WASAPI:

```csharp
using Sonora.Audio.Output;

await player.ConfigureOutputAsync(new AudioOutputConfiguration
{
    Mode = AudioOutputMode.Exclusive,
    ExclusiveFallback = ExclusiveModeFallback.Shared,
});

// Inspect requested vs effective mode after configure / fallback
Console.WriteLine(player.EffectiveOutputMode);
Console.WriteLine(player.OutputFallbackReason);

await player.ConfigureOutputAsync(new AudioOutputConfiguration
{
    Mode = AudioOutputMode.Shared,
});
```

With `ExclusiveFallback.None`, a failed exclusive open leaves the player `Unavailable` but keeps the loaded track so you can retry without `LoadAsync`.

## Read timing information

```csharp
Console.WriteLine(player.State);
Console.WriteLine(player.SourcePath);
Console.WriteLine(player.Duration);
Console.WriteLine(player.Position);           // estimated audible (active track)
Console.WriteLine(player.BufferedPosition);   // buffered decode frontier
```

`Position` is an audible estimate — not the last seek request. Use `SeekWithResult` for requested vs actual decoded landings (`ActualPosition` is null when accuracy is `Unknown`). Gapless handoffs update identity at the output-consumption boundary and are not ear-sample-accurate. See the README for accuracy caveats.

## Console sample

```bash
dotnet run --project samples/Sonora.ConsolePlayer -- "C:\music\track.wav"
```

Keys: Space play/pause, S stop, arrows seek, +/- volume, Q quit.

## Equalizer and preamp

```csharp
using Sonora.Audio.DSP;

// Recommended order: Preamp → Equalizer → Limiter
player.Effects.AddPreamp(-2f);   // dB; separate from player.Volume

var eq = player.Effects.AddEqualizer(); // six-band default
eq.SetBandGain(1, 4f);
eq.ApplyPreset(EqualizerPresets.BassBoost);

player.Effects.AddLimiter();     // sample-peak; default LookaheadMs=5
```

Effects process after the PCM buffer so slider changes are audible immediately. Use `SonoraWindows.CreateEngine()` so the Windows effect factory is registered. See [limiter.md](limiter.md) for the sample-peak contract.

## Supported formats

On Windows, Sonora supports WAV, AIFF, MP3, ADTS AAC, M4A (AAC and ALAC), WMA, FLAC, Ogg Vorbis, Ogg Opus, and WebM Opus when the host can decode them. See the README format table for backends and caveats.

FLAC / Media Foundation seeking is fixture-verified for correct audio content after seeks. Those backends do not report `AudioSeekAccuracy.Exact` from container timestamps alone; WAV/AIFF may report Exact when byte-position math verifies the landing.
