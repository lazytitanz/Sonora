<div align="center">

<img src="img/Sonora.png" alt="Sonora" width="160" />

# Sonora

### A reliable .NET audio engine with a clean public API.

**Independent, open-source C# playback for Windows apps — orchestration, buffering, and DSP without exposing NAudio types.**

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![Version](https://img.shields.io/badge/version-1.0.0--rc.1-blue)](CHANGELOG.md)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

[Why Sonora?](#why-sonora) · [Quick start](#quick-start) · [Formats](#supported-audio-formats) · [DSP](#dsp-effects) · [Architecture](#how-it-works) · [Build](#build-and-test) · [Docs](#documentation)

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
- **Wide format coverage** — WAV, AIFF, MP3, AAC, M4A, FLAC, WMA, ALAC, Ogg Vorbis, Ogg Opus, WebM Opus
- **Gapless playback** — `PreloadNextAsync` + seamless hard-cut transition on one WASAPI session
- **Dual position tracking** — `Position` (audible estimate) and `BufferedPosition` (decode frontier)
- **Live volume and DSP** — `Volume`, EQ, and preamp apply on the output path without waiting for the PCM buffer to drain
- **Metadata** — read tags and optional embedded artwork without creating a player or opening WASAPI
- **HTTP streaming** — play `http`/`https` URLs with bounded buffering, cancellation, and honest seek reporting
- **MIT licensed** — packages prepared for NuGet (`Sonora.Audio`, `Sonora.Audio.Windows`)

> [!NOTE]
> Current package version is **`1.0.0-rc.1`**. Packages are not published to NuGet.org automatically. Reference the projects or pack locally (see [Build and test](#build-and-test)). Promoting RC → `1.0.0` requires the [production checklist](docs/production-checklist.md).

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
Console.WriteLine(player.HasKnownDuration);  // false for live/unknown-length sources
```

### HTTP / HTTPS

```csharp
using Sonora.Audio.Sources;

await player.LoadAsync(new HttpAudioSource(new Uri("https://example.com/audio.mp3")));
player.NetworkStatusChanged += (_, _) =>
    Console.WriteLine($"{player.NetworkTransportStatus} buf={player.NetworkBytesBuffered}");
player.Play();
```

String `LoadAsync(path)` remains **file-only** — URLs must use `HttpAudioSource` explicitly.

### Metadata (independent of playback)

```csharp
using Sonora.Audio.Metadata;
using Sonora.Audio.Windows;

var reader = SonoraWindows.CreateMetadataReader(); // or new AudioMetadataReader()

var meta = reader.Read(@"C:\music\song.flac");
Console.WriteLine($"{meta.Title} — {string.Join(", ", meta.Artists)}");
Console.WriteLine(meta.Duration);

var withArt = reader.Read(
    @"C:\music\song.flac",
    new AudioMetadataReadOptions { IncludeArtwork = true });

var cover = withArt.Artwork.FirstOrDefault(a => a.Kind == AudioArtworkKind.FrontCover)
    ?? withArt.Artwork.FirstOrDefault();
```

Metadata uses managed [ATL.NET](https://github.com/Zeugma440/atldotnet) (`z440.atl.core` **7.19.0**, MIT; transitive `Ude.NetStandard` for charset detection). ATL types are not part of Sonora’s public API. Playback remains conceptually separate; the core package now carries this metadata-only dependency.

### Playback controls

| API | Behavior |
| --- | --- |
| `LoadAsync` | Opens a file or `AudioSource` (file / HTTP); releases the previous source |
| `Play` | Starts or resumes |
| `Pause` | Preserves position |
| `Stop` | Stops and resets to the beginning (does not raise completion) |
| `Seek` | Seeks when the decoder supports it (preserves a safe next track). Long decoder work runs off the command gate so Pause/Stop/Dispose stay responsive |
| `SeekWithResult` | Same as `Seek`, returns `AudioSeekResult` (requested / actual / accuracy) |
| `Volume` | Linear gain in `[0, 1]` (applied post-ring on the output path; immediate) |

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

### Metadata coverage (0.8.0)

Verified with fixtures via ATL.NET (tags / duration / technical props / artwork). Claims are read-only extraction, not tag writing.

| Format | Basic tags | Duration | Sample rate / channels | Artwork | Notes |
| --- | --- | --- | --- | --- | --- |
| WAV | Verified | Verified | Verified | Verified | RIFF INFO / ID3 when present |
| AIFF | Verified | Verified | Verified | Partial | Common chunks / ID3 when present |
| MP3 | Verified | Verified | Verified | Partial | ID3v1/v2; VBR duration approximate |
| FLAC | Verified | Verified | Verified | Partial | Vorbis comments; `IsLossless == true` |
| Ogg Vorbis | Verified | Verified | Verified | Partial | |
| Ogg Opus | Verified | Verified | Verified | Partial | |
| M4A AAC | Verified | Verified | Verified | Partial | MP4 atoms |
| ALAC | Verified | Verified | Verified | Partial | ATL reports `IsLossless == false` (limitation) |
| WMA | Verified | Verified | Verified | Partial | ASF |
| WebM Opus | Verified | Verified | Unverified | Partial | Sample rate / channels often omitted by ATL |

“Partial” artwork means container support exists and Sonora returns owned bytes when present; not every fixture embeds covers.

---

## DSP effects

Per-player effect rack via `player.Effects`. Processing runs **after** the PCM ring on the output path so EQ/preamp changes apply immediately. NAudio implementations stay inside `Sonora.Audio.Windows`.

```csharp
using Sonora.Audio.DSP;
using Sonora.Audio.Windows;

using var engine = SonoraWindows.CreateEngine();
using var player = engine.CreatePlayer();
await player.LoadAsync(@"C:\music\song.flac");

// Recommended order: Preamp → Equalizer → Limiter
player.Effects.AddPreamp(-3f);   // dB; distinct from player.Volume

var eq = player.Effects.AddEqualizer(); // default six-band
eq.SetBandGain(0, 3f);
eq.SetBandGain(5, -2f);
eq.ApplyPreset(EqualizerPresets.BassBoost);

player.Effects.AddLimiter();    // sample-peak; default LookaheadMs=5
player.Play();
```

| Effect | Notes |
| --- | --- |
| **Preamp** | Gain in dB; distinct from `player.Volume` |
| **Equalizer** | Configurable bands (default 6); peaking / low-shelf / high-shelf; presets in `EqualizerPresets` |
| **Limiter** | Sample-peak ceiling; default `LookaheadMs=5` guaranteeing; see [docs/limiter.md](docs/limiter.md) |

Recommended rack order: **Preamp → EQ → Limiter**. Settings persist across track changes; filter state resets on seek/stop/load (not on gapless joins). Structural chain edits use immutable snapshots; parameter updates apply on the audio thread at block boundaries.

<details>
<summary>Limiter limitations</summary>

Sample-peak only (not true-peak / inter-sample; no soft clip). Default `LookaheadMs=5` is the guaranteeing multi-sample setting under Sonora’s tests. Explicit `LookaheadMs=0` is minimum-latency: measured with NAudio 3.3.0 it still uses ≥1 sample of delay and held the sample-peak ceiling for impulses, mid-buffer steps, and sparse bursts, but it is not a true-peak guarantee. Prefer sensible preamp/EQ gains; see [docs/limiter.md](docs/limiter.md).

</details>

---

## How it works

```text
LoadAsync → decoder factory → format sniff → IAudioDecoder
  → decode pump → [optional IAudioProcessor] → PCM ring
  → output pull → Volume → player EffectRack → WASAPI (shared mode)
```

| Layer | Role |
| --- | --- |
| `Sonora.Audio` | Public API, player, ring buffer, decode pump, DSP contracts |
| `Sonora.Audio.Windows` | NAudio/WASAPI output, Media Foundation + managed codecs, NAudio-backed effects |

More detail: [docs/architecture.md](docs/architecture.md) · [docs/getting-started.md](docs/getting-started.md)

---

## Documentation

| Doc | Topic |
| --- | --- |
| [docs/getting-started.md](docs/getting-started.md) | Consumer quick start |
| [docs/architecture.md](docs/architecture.md) | Pipeline and layering |
| [docs/versioning.md](docs/versioning.md) | SemVer, RC process, 0.9 API freeze |
| [docs/codec-portability.md](docs/codec-portability.md) | Format / HTTP / platform matrix |
| [docs/limiter.md](docs/limiter.md) | Sample-peak limiter contract |
| [docs/hardware-testing.md](docs/hardware-testing.md) | Opt-in WASAPI tests |
| [docs/production-checklist.md](docs/production-checklist.md) | Gates for `rc.1` → `1.0.0` |
| [docs/performance-baseline.md](docs/performance-baseline.md) | Measured microbenchmarks (host-local) |
| [SECURITY.md](SECURITY.md) | Reporting, SSRF responsibility |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | ATL, Ude, NAudio, NVorbis, Concentus |

---

## Project structure

```text
Sonora/
├── src/Sonora.Audio              # Platform-independent core
├── src/Sonora.Audio.Windows      # NAudio / WASAPI backend
├── tests/                        # Unit, Windows, and package-consumer tests
├── samples/Sonora.ConsolePlayer  # Interactive console demo
├── samples/Sonora.TestPlayer     # Vetra + Bootstrap Windows validation UI (see its README)
└── docs/                         # Architecture, versioning, portability, limiter
```

---

## Build and test

```bash
# Windows: pack Vetra into deps/nuget first (pinned script; required for Sonora.TestPlayer)
./scripts/pack-vetra.ps1

dotnet build Sonora.slnx -c Release
dotnet test Sonora.slnx -c Release --filter "Category!=Hardware&Category!=Soak"
dotnet pack src/Sonora.Audio/Sonora.Audio.csproj -c Release
dotnet pack src/Sonora.Audio.Windows/Sonora.Audio.Windows.csproj -c Release
```

The graphical Test Player is documented in [samples/Sonora.TestPlayer/README.md](samples/Sonora.TestPlayer/README.md). CI builds it but does not launch the UI. Hardware-dependent engine tests run only when `SONORA_RUN_HARDWARE_TESTS=1` (see [docs/hardware-testing.md](docs/hardware-testing.md)).

### Installation (local packages)

```bash
dotnet add package Sonora.Audio
dotnet add package Sonora.Audio.Windows
```

---

## Current limitations

- **Playback is Windows-only** (WASAPI shared by default; optional exclusive). `Sonora.Audio` builds and unit-tests on Linux; decode/output backends live in `Sonora.Audio.Windows`
- Exclusive mode is not bit-perfect when DSP, volume, or format conversion are active
- Device / mode switches are not sample-accurate; old-endpoint padding is skipped and a short gap may be heard
- Follow-default uses notification-driven rebinds (Multimedia role), not OS stream-routing-as-correctness
- No HLS/DASH, podcast feeds, or online metadata lookup
- HTTP seeking requires verified byte-range support; MF formats need a seekable body (forward-only MF is not claimed)
- Applications accepting arbitrary URLs should apply SSRF / egress controls ([SECURITY.md](SECURITY.md))
- Metadata does not invent titles from file names; ATL `UseFileNameWhenNoTitle` is disabled once on first Sonora metadata use
- ATL may report ALAC as lossy; WebM Opus may omit sample rate / channels; artwork write/edit is out of scope
- Gapless joins are continuous PCM hard-cuts; encoder delay/padding trimming for lossy codecs is not applied
- Audible track-change timing is not sample-accurate under shared-mode WASAPI
- FLAC / Media Foundation seeking is reliable for playback; sample-accurate (`Exact`) seeking is not claimed for MF-backed formats
- Bare Ogg Opus uses managed Concentus (not MF); WebM Opus depends on host Media Foundation codecs (WebM Vorbis is not claimed)
- The limiter is **sample-peak** only (not true-peak); default `LookaheadMs=5` is the guaranteeing setting. Explicit `LookaheadMs=0` held sample-peak ceilings in measured tests but is not true-peak safe ([docs/limiter.md](docs/limiter.md))
- Synchronous `Dispose` may still block off-gate while waiting for output quiescence or the decode pump; prefer `DisposeAsync` from async/UI contexts. Concurrent Dispose/Stop/Shutdown share one completion. Prefer calling `Seek` / `SeekWithResult` off a UI thread when targeting Media Foundation formats (m4a/mp3/flac/…) — the engine will not hold the command gate during the decoder seek, but the calling thread can still block for the seek itself
- WASAPI hardware tests are tagged `Hardware` (excluded from default CI; require `SONORA_RUN_HARDWARE_TESTS=1`). Hardware/soak results are not claimed unless actually run
- Host-local ring/DSP/metadata microbenchmarks are recorded in [docs/performance-baseline.md](docs/performance-baseline.md); WASAPI callback latency, underrun soak, and exclusive-mode timings are not yet measured

## Roadmap

- Stronger WebM portability across Windows SKUs
- Additional DSP effects (compressor, reverb, …) on the existing rack
- Crossfade on the existing transition stage
- Encoder-delay/padding aware gapless where metadata is available
- Additional platform backends

## License

Sonora is licensed under the [MIT License](LICENSE).
