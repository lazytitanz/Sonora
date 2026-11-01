# Changelog

All notable changes to Sonora will be documented in this file.

## 0.6.0 — 2026-11-01

### Added

- Optional WASAPI **exclusive-mode** output via `AudioPlayer.ConfigureOutputAsync(AudioOutputConfiguration)`
- Platform-neutral `AudioOutputMode`, `ExclusiveModeFallback`, `ExclusiveFallbackReason`, and `AudioOutputFailureKind`
- Requested vs effective mode inspection (`OutputConfiguration`, `EffectiveOutputMode`, `OutputFallbackReason`)
- `OutputConfigurationChanged` event (raised off the command gate)
- Non-destructive exclusive format probing before rebind (`IAudioOutputFactory.ProbeOutputFormatAsync`)
- Configurable shared-mode fallback on recoverable exclusive failures (device busy, exclusive restricted, unsupported format, Init failure)
- Distinction between device contention (`DeviceBusy`) and exclusive-access policy (`ExclusiveModeRestricted`) when Windows HRESULTs permit
- Session-format rebuild when exclusive negotiation requires a different rate/channels; next-track path preserved with rebuild or explicit `NextTrackState.Failed`
- Windows exclusive path: `WasapiPlayerBuilder.WithExclusiveMode`, latency as a requested target (default 50 ms), buffer-alignment Init retries
- Core output-mode tests and Windows HRESULT mapping tests

### Changed

- `IAudioOutputFactory` adds `CreateOutputAsync(AudioOutputCreateRequest, …)` and format probing; existing deviceId overload remains shared-mode
- Strict exclusive failure leaves the player `Unavailable` but preserves track, position intent, DSP, and next-track selection for retry without reload
- Shared fallback never starts until a partially initialized exclusive output is quiesced and disposed

### Notes

- Shared mode remains the default; exclusive must be requested explicitly.
- Exclusive mode does **not** imply bit-perfect delivery (DSP, volume, resampling, and format conversion may alter samples).
- Mode and device switches may briefly interrupt audible playback; v0.5.0 Shutdown/generation guarantees are preserved.
- Gapless session locking is unchanged across next-track joins; format renegotiation occurs only on output rebind.

## 0.5.0 — 2026-11-01

### Added

- Platform-neutral output device API: `IAudioOutputDevices`, `AudioOutputDevice`, `AudioDeviceState`
- `AudioEngine.OutputDevices` enumeration and change notifications (`DevicesChanged`, `DefaultDeviceChanged`)
- `AudioPlayer.SetOutputDeviceAsync(string? deviceId)` — `null` follows the system default
- Preferred vs active endpoint separation (`PreferredOutputDeviceId`, `ActiveOutputDeviceId`)
- `OutputDeviceStatus` and `OutputRecoveryIntent` (independent of `PlaybackState`)
- `OutputDeviceChanged` event with explicit change reasons
- Windows Core Audio enumeration via NAudio `MMDeviceEnumerator` (Multimedia default render role)
- Notification-driven follow-default rebinds and explicit-device fallback to the Multimedia default
- Output-only rebind that preserves decode pipelines, gapless next track, volume, and DSP configuration
- Deterministic device-management tests (hardware-free) plus Windows state-mapping tests
- `AudioDeviceException` for invalid device selection
- `IAudioOutput.Shutdown()` quiescence contract: after successful return, the old output must not read PCM again
- `OutputDeviceStatus.Switching` while no output is attached during a rebind
- Holdable fake-output regression tests for overlapping switches, Stop/Dispose during teardown, and teardown failure with a live consumer

### Changed

- `IAudioOutputFactory` now exposes `CreateOutputAsync(string? deviceId, CancellationToken)` (replaces `CreateDefaultOutput`)
- `IAudioOutput` reports `EndpointId` when known
- Output switches detach under the gate, join/shutdown outside the gate, then attach only after a generation check; teardown failure leaves the player `Unavailable` and does not start a replacement
- Superseded `SetOutputDeviceAsync` calls throw `OperationCanceledException` instead of reporting success when no output was attached

### Notes

- Device switches reinitialize WASAPI and may briefly interrupt audible playback; endpoint padding already submitted to the old device is skipped (typically on the order of `CurrentLatency`). Correctness and resource ownership are preferred over minimizing interruption.
- Follow-default correctness uses notification-driven rebinds with `WithDevice(Multimedia default)` — NAudio stream routing is not relied on for correctness.
- When an explicitly preferred device is lost, Sonora falls back to the system default and does **not** auto-restore when the preferred endpoint returns.
- Explicit `Stop`, `LoadAsync`, and `Dispose` clear recovery intent so auto-resume cannot revive a stopped session.
- Existing codecs, DSP, gapless playback, and transport APIs are preserved.

## 0.4.0 — 2026-11-01

### Added

- Gapless next-track preloading via `AudioPlayer.PreloadNextAsync` / `ClearNext`
- `NextTrackState`, `NextTrackPath`, and `SourcePath` for preload and active-track identity
- `TrackTransitioned` event for consumption-boundary handoffs (deferred off the audio callback)
- Dual decode pipelines with a spanning hard-cut `TransitionSampleSource` on one continuous WASAPI session
- Session-locked output format with Windows NAudio WDL resampling (`WindowsAudioFormatConverter`) and core channel mixing
- Core gapless / preload / spanning-buffer / lifecycle tests (hardware-free)

### Notes

- Applications select the next track; Sonora does not manage playlists.
- Seek preserves a safe preloaded next track; `Stop` / `LoadAsync` / `Dispose` clear it.
- DSP effect settings persist across gapless joins; processing state is not reset at the boundary.
- Continuous output without device restart is supported for prepared joins. Encoder-delay/padding-corrected gapless for lossy codecs is not claimed. `Position` and transition delivery remain estimates under shared-mode WASAPI.

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

### Fixed

- Explicit `Stop()` no longer raises `PlaybackCompleted` when the output device reports end-of-stream as a side effect of stopping (WASAPI/NAudio `PlaybackStopped` race).

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
- WebM Opus via Media Foundation when the host OS demuxes/decodes the file (WebM Vorbis not claimed)
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
