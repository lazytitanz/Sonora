<div align="center">

<img src="img/Sonora.png" alt="Sonora" width="160" />

# Sonora

### A reliable .NET audio engine with a clean public API.

**Independent, open-source C# playback for Windows apps — orchestration, buffering, and DSP without exposing NAudio types.**

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![Version](https://img.shields.io/badge/version-0.6.0-blue)](CHANGELOG.md)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

[Why Sonora?](#why-sonora) · [Quick start](#quick-start) · [Formats](#supported-audio-formats) · [DSP](#dsp-effects) · [Architecture](#how-it-works) · [Build](#build-and-test)

</div>

---

## Why Sonora?

Sonora is a standalone audio engine for .NET applications. The Windows backend uses [NAudio](https://github.com/naudio/NAudio) with WASAPI shared-mode output by default (optional exclusive mode), while Sonora owns playback state, bounded PCM buffering, decoding off the audio callback, and a per-player DSP rack.

It is built for apps like [EMP](https://github.com/lazytitanz/EMP) but stays completely application-agnostic — no UI, playlists, or library scanning.

| Goal | What that means |
| --- | --- |
| **Simple API** | Load, play, pause, seek, volume — minimal setup |
| **Clean boundaries** | Platform-independent core; NAudio stays in `Sonora.Audio.Windows` |
| **Predictable playback** | Deterministic state, resource disposal, and completion semantics |
| **Real-time safe** | Decoding and file I/O stay off the WASAPI callback |
| **Extensible DSP** | Equalizer, preamp, and limiter on a per-player effect rack |
| **Gapless joins** | Preload one next track; continuous WASAPI without device restart |

### At a glance

- **.NET 10** — `Sonora.Audio` (`net10.0`) and `Sonora.Audio.Windows` (`net10.0-windows10.0.19041.0`)
- **Wide format coverage** — WAV, AIFF, MP3, AAC, M4A, FLAC, WMA, ALAC, Ogg Vorbis, Ogg Opus, WebM audio
- **Gapless playback** — `PreloadNextAsync` + seamless hard-cut transition on one WASAPI session
- **Dual position tracking** — `Position` (audible estimate) and `BufferedPosition` (decode frontier)
- **Live DSP** — EQ/preamp changes apply on the output path without waiting for the PCM buffer to drain
- **MIT licensed** — packages prepared for NuGet (`Sonora.Audio`, `Sonora.Audio.Windows`)

> [!NOTE]
> Packages are not published to NuGet.org automatically during early development. Reference the projects or pack locally (see [Build and test](#build-and-test)).

---

## Quick start

**Requirements:** Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```csharp
using Sonora.Audio;
using Sonora.Audio.Windows;

using var engine = SonoraWindows.CreateEngine();
using var player = engine.CreatePlayer();

await player.LoadAsync(@"C:\music\song.mp3");
player.Volume = 0.75f;
player.Play();

Console.WriteLine(player.Position);          // estimated audible position
Console.WriteLine(player.BufferedPosition);  // decode/buffer frontier
Console.WriteLine(player.Duration);
```

### Playback controls

| API | Behavior |
| --- | --- |
| `LoadAsync` | Opens a file; releases the previous source |
| `Play` | Starts or resumes |
| `Pause` | Preserves position |
| `Stop` | Stops and resets to the beginning (does not raise completion) |
| `Seek` | Seeks when the decoder supports it (preserves a safe next track) |
| `SeekWithResult` | Same as `Seek`, returns `AudioSeekResult` (requested / actual / accuracy) |
| `Volume` | Linear gain in `[0, 1]` |

**Events:** `StateChanged`, `PlaybackCompleted`, `TrackTransitioned`, `OutputDeviceChanged`, `OutputConfigurationChanged`, `ErrorOccurred`, `Underrun`. Raised on worker threads (not marshaled to a UI sync context). `TrackTransitioned` is never raised on the WASAPI callback and may fire after the next track’s samples have already entered the output pipeline.

### Output devices

Enumerate endpoints and select an output (Windows Multimedia default render role):

```csharp
using Sonora.Audio.Devices;

using var engine = SonoraWindows.CreateEngine();
using var player = engine.CreatePlayer();

foreach (var device in engine.OutputDevices.GetDevices())
{
    Console.WriteLine($"{device.Name}  default={device.IsDefault}  state={device.State}");
}

// Explicit endpoint
await player.SetOutputDeviceAsync(engine.OutputDevices.GetDevices()
    .First(d => d.State == AudioDeviceState.Active && !d.IsDefault).Id);

// Follow Windows default again
await player.SetOutputDeviceAsync(null);

engine.OutputDevices.DevicesChanged += (_, _) => { /* refresh UI list */ };
engine.OutputDevices.DefaultDeviceChanged += (_, e) =>
{
    Console.WriteLine($"Default: {e.PreviousDeviceId} → {e.NewDeviceId}");
};
```

| Behavior | Detail |
| --- | --- |
| Default | Follow system Multimedia default until the app selects an id |
| Switch | Reinitializes WASAPI; brief interruption expected; ring / DSP / next track preserved |
| Preferred lost | Fall back to system default; preferred id kept; **no** auto-restore when it returns |
| Recovery intent | Playing through device loss → resume when output returns; cleared by `Stop` / `LoadAsync` / `Dispose` |

### Exclusive mode (optional)

Shared mode is the default. Request exclusive WASAPI explicitly:

```csharp
using Sonora.Audio.Output;

await player.ConfigureOutputAsync(new AudioOutputConfiguration
{
    Mode = AudioOutputMode.Exclusive,
    ExclusiveFallback = ExclusiveModeFallback.Shared, // or None for strict
});

Console.WriteLine(player.EffectiveOutputMode);      // Exclusive, or Shared after fallback
Console.WriteLine(player.OutputFallbackReason);

// Return to shared
await player.ConfigureOutputAsync(new AudioOutputConfiguration
{
    Mode = AudioOutputMode.Shared,
});
```

| Behavior | Detail |
| --- | --- |
| Strict (`ExclusiveFallback.None`) | Never silently becomes shared; failure → `Unavailable` with track/position/DSP/next preserved for retry |
| Shared fallback | Used only for recoverable exclusive failures after the exclusive instance is quiesced |
| Format probe | Non-destructive; does not dual-consume the PCM session |
| Bit-perfect | **Not** claimed — DSP, volume, and conversion may alter samples |

### Gapless playback

Application-controlled next-track selection (no playlist manager):

```csharp
using var engine = SonoraWindows.CreateEngine();
using var player = engine.CreatePlayer();

await player.LoadAsync(@"C:\Music\Track01.flac");
await player.PreloadNextAsync(@"C:\Music\Track02.flac");
player.Play();

player.TrackTransitioned += (_, e) =>
{
    // Previous track finished at the output-consumption boundary; next is now active.
    // Delivery is deferred off the audio thread and is not ear-sample-accurate.
};
```

| Behavior | Detail |
| --- | --- |
| Preload | Opens the next decoder, converts to the session format, fills a **bounded** ring (`BufferDuration`) |
| Ready / Failed | Inspect `NextTrackState` and `NextTrackPath` |
| Replace | Another `PreloadNextAsync` cancels/disposes the previous pending track |
| Clear | `ClearNext()`, `Stop()`, `LoadAsync()`, and `Dispose()` clear the slot; **Seek preserves** a safe next track |
| Transition | When the current track drains and next is `Ready`, Sonora hard-cuts into B without stopping WASAPI |
| Not ready | Natural `PlaybackCompleted` as before (exactly once) |
| DSP | Same effect rack and settings continue across the join (filter state is not reset at the boundary) |

Mismatched sample rates/channels are converted on the decode pump (NAudio WDL on Windows). Continuous output without a device restart is guaranteed for prepared joins; **encoder-delay/padding-corrected** gapless for MP3/AAC/Opus is **not** claimed unless metadata is verified.

### Position accuracy

- **`BufferedPosition`** — timeline of the latest sample written into the **active** track’s PCM ring (decode frontier).
- **`Position`** — estimated **audible** position within the active track (device clock when available, rebased across seeks and gapless handoffs; accounts for reported output latency when available). Not the last seek request and not a verified decoded cursor.
- **`SeekWithResult`** — reports `RequestedPosition`, optional `ActualPosition` (null when `Unknown`), and `AudioSeekAccuracy`. Timestamps are source-decoder frame times (floor frame rounding).
- **Track identity** (`SourcePath` / `Duration`) — updates at the **output-consumption** handoff, which can precede what the listener hears by device buffering/latency.

| Accuracy | Meaning |
| --- | --- |
| `Exact` | First retained source frame matches the requested frame (proven; WAV/AIFF when byte math verifies) |
| `Approximate` | Landed near the request with evidence-backed `ActualPosition` (not mere container timestamps) |
| `Unknown` | Reposition attempted; actual landing could not be established (`ActualPosition` is null) |

Shared-mode resampling, device padding, and underruns introduce error. Positions and transition timing are suitable for UI scrubbers, but are **not** sample-accurate broadcast / lip-sync grade. Media Foundation seeks are never labeled `Exact` solely because a timestamp was accepted.

### Console sample

```bash
dotnet run --project samples/Sonora.ConsolePlayer -c Release -- path\to\file.mp3
```

---

## Supported audio formats

Formats are selected by container/header sniffing (not extension alone). Only formats that pass decode, seek, duration, and completion fixtures are claimed.

| Format | Backend | Notes |
| --- | --- | --- |
| WAV | `WaveFileReader` | PCM |
| AIFF / AIF | `AiffFileReader` | PCM (NAudio.Core; no extra package) |
| MP3 | Media Foundation | |
| ADTS AAC (`.aac`) | Media Foundation | |
| M4A (AAC) | Media Foundation | MPEG-4 AAC |
| M4A (ALAC) | Media Foundation | Apple Lossless in `.m4a` (Win10+) |
| WMA | Media Foundation | ASF / WMA |
| FLAC | Media Foundation | Content-verified seeks; accuracy is `Unknown`/`Approximate`, never Exact from MF timestamps |
| Ogg Vorbis | NVorbis + NAudio.Vorbis | Managed MIT |
| Ogg Opus (`.opus`) | Concentus + Concentus.Oggfile | Managed MIT; MF does not open bare `.opus` reliably |
| WebM (Opus) | Media Foundation | Fixture-verified; WebM Vorbis is not claimed (MF codecs vary by Windows SKU) |

---

## DSP effects

Per-player effect rack via `player.Effects`. Processing runs **after** the PCM ring on the output path so EQ/preamp changes apply immediately. NAudio implementations stay inside `Sonora.Audio.Windows`.

```csharp
using Sonora.Audio.DSP;
using Sonora.Audio.Windows;

using var engine = SonoraWindows.CreateEngine();
using var player = engine.CreatePlayer();
await player.LoadAsync(@"C:\music\song.flac");

var eq = player.Effects.AddEqualizer(); // default six-band
eq.SetBandGain(0, 3f);
eq.SetBandGain(5, -2f);
eq.ApplyPreset(EqualizerPresets.BassBoost);

player.Effects.AddPreamp(-3f);   // dB; distinct from player.Volume
player.Effects.AddLimiter();    // optional clipping protection

player.Play();
```

| Effect | Notes |
| --- | --- |
| **Equalizer** | Configurable bands (default 6); peaking / low-shelf / high-shelf; presets in `EqualizerPresets` |
| **Preamp** | Gain in dB; distinct from `player.Volume` |
| **Limiter** | Conservative peak limiting; zero lookahead by default |

Settings persist across track changes; filter state resets on seek/stop/load. Structural chain edits use immutable snapshots; parameter updates apply on the audio thread at block boundaries.

<details>
<summary>Limiter limitations</summary>

Zero-lookahead limiting is not a true look-ahead brick-wall or true-peak limiter. Extreme EQ boosts can still sound compressed or briefly over. Prefer sensible preamp/EQ gains rather than relying on the limiter alone.

</details>

---

## How it works

```text
LoadAsync → decoder factory → format sniff → IAudioDecoder
  → decode pump → [optional IAudioProcessor] → Volume → PCM ring
  → output pull → player EffectRack → WASAPI (shared mode)
```

| Layer | Role |
| --- | --- |
| `Sonora.Audio` | Public API, player, ring buffer, decode pump, DSP contracts |
| `Sonora.Audio.Windows` | NAudio/WASAPI output, Media Foundation + managed codecs, NAudio-backed effects |

More detail: [docs/architecture.md](docs/architecture.md) · [docs/getting-started.md](docs/getting-started.md)

---

## Project structure

```text
Sonora/
├── src/Sonora.Audio              # Platform-independent core
├── src/Sonora.Audio.Windows      # NAudio / WASAPI backend
├── tests/                        # Unit and Windows tests
├── samples/Sonora.ConsolePlayer  # Interactive demo
└── docs/                         # Architecture and getting started
```

---

## Build and test

```bash
dotnet build Sonora.slnx -c Release
dotnet test Sonora.slnx -c Release --filter "Category!=Hardware"
dotnet pack src/Sonora.Audio/Sonora.Audio.csproj -c Release
dotnet pack src/Sonora.Audio.Windows/Sonora.Audio.Windows.csproj -c Release
```

Hardware-dependent tests run only when `SONORA_RUN_HARDWARE_TESTS=1`.

### Installation (local packages)

```bash
dotnet add package Sonora.Audio
dotnet add package Sonora.Audio.Windows
```

---

## Current limitations

- Windows only for output (WASAPI shared by default; optional exclusive)
- Exclusive mode is not bit-perfect when DSP, volume, or format conversion are active
- Device / mode switches are not sample-accurate; old-endpoint padding is skipped and a short gap may be heard
- Follow-default uses notification-driven rebinds (Multimedia role), not OS stream-routing-as-correctness
- No metadata extraction or network streaming
- Gapless joins are continuous PCM hard-cuts; encoder delay/padding trimming for lossy codecs is not applied
- Audible track-change timing is not sample-accurate under shared-mode WASAPI
- FLAC / Media Foundation seeking is reliable for playback; sample-accurate (`Exact`) seeking is not claimed for MF-backed formats
- Bare Ogg Opus uses managed Concentus (not MF); WebM Opus depends on host Media Foundation codecs
- WASAPI playback integration tests are tagged `Hardware` (skipped in default CI)
- Zero-lookahead limiting can allow brief overs under extreme boosts

## Roadmap

- Stronger WebM portability across Windows SKUs
- Additional DSP effects (compressor, reverb, …) on the existing rack
- Crossfade on the existing transition stage
- Encoder-delay/padding aware gapless where metadata is available
- Additional platform backends

## License

Sonora is licensed under the [MIT License](LICENSE).
