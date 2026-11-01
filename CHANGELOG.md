# Changelog

All notable changes to Sonora will be documented in this file.

## 0.3.0 — 2026-11-01

### Added

- Per-player DSP effect rack (`AudioPlayer.Effects`) with add/remove/reorder/bypass
- Multiband equalizer (default six-band; peaking / low-shelf / high-shelf) via NAudio `Equalizer`
- Preamp gain in dB via NAudio `GainEffect` (distinct from `Volume`)
- Optional peak limiter via NAudio `LimiterEffect` (0 dBFS ceiling, zero lookahead by default)
- Built-in equalizer presets (`EqualizerPresets`)
- Post-ring pull-path processing so live EQ/preamp changes do not wait for the PCM buffer to drain
- Thread-safe parameter handoff (`ApplyPendingUpdates` on the audio thread) and deferred effect disposal
- Core rack tests and Windows frequency / gain / peak limiter tests

### Notes

- NAudio DSP types remain internal to `Sonora.Audio.Windows`; core public APIs stay NAudio-free.
- Effect settings persist across track changes; processing state resets on seek/stop/load.
- Zero-lookahead limiting can allow brief overs and is not equivalent to a look-ahead true-peak limiter.
- Existing playback APIs and codec support are unchanged.

## 0.2.0 — 2026-11-01

### Added

- Sniff-based Windows decoder selection (`DecoderSelector`) for container-aware routing
- AIFF / AIF decoding via NAudio `AiffFileReader` (no new dependencies)
- ADTS AAC (`.aac`), WMA, and M4A (ALAC) via Media Foundation (fixture-verified)
- Ogg Vorbis via **NVorbis** + **NAudio.Vorbis** (MIT, managed)
- Ogg Opus via **Concentus** + **Concentus.Oggfile** (MIT, managed)
- WebM audio (Opus / Vorbis) via Media Foundation when the host OS demuxes/decodes the file
- Shared `WaveStreamAudioDecoder` adapter for WaveStream-backed formats
- Codec expansion fixtures and decode/seek/completion tests

### Notes

- Bare `.opus` / `.ogg` are not claimed through Media Foundation (MF rejects them on clean Windows); managed backends are required.
- WebM support depends on Media Foundation on the host; verified on Windows 10/11 test hosts without optional Store codec packs for the checked fixtures.
- Public playback API is unchanged from 0.1.0.

## 0.1.0 — 2026-11-01

### Added

- `Sonora.Audio` platform-independent core with `AudioEngine` / `AudioPlayer`
- Bounded float32 PCM ring buffer and background decode pump
- Dual position tracking: `Position` (audible estimate) and `BufferedPosition`
- Windows backend (`Sonora.Audio.Windows`) using NAudio 3.3.0
- WASAPI shared-mode output via `WasapiPlayer` / `WasapiPlayerBuilder`
- WAV, MP3, M4A (AAC), and FLAC decoding (Media Foundation for compressed formats)
- Reliable FLAC seeking via immediate MF reposition (`RepositionInRead=false`) with reader recreate only as EOF/invalid-state fallback
- Console sample player
- xUnit tests for core playback semantics and Windows fixtures (including segmented FLAC PCM seek checks)
- NuGet pack configuration and GitHub Actions CI

### Notes

- FLAC seeking is content-verified but not advertised as sample-accurate under Media Foundation.
- Output uses the default render device only.
- Exclusive-mode WASAPI is out of scope for 0.1.0.
