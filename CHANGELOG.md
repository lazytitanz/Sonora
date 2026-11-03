# Changelog

All notable changes to Sonora will be documented in this file.

## 1.0.0-rc.1 — 2026-11-01

### Added

- Public API snapshot tests and baselines for `Sonora.Audio` / `Sonora.Audio.Windows` (v1.0 RC freeze surface)
- Package-consumer smoke project (`tests/Sonora.PackageConsumer`) restoring packed nupkgs from a local feed
- Docs: versioning, codec portability, hardware testing, limiter contract, production checklist, performance baseline template
- `SECURITY.md` and `THIRD-PARTY-NOTICES.md`
- SourceLink (`Microsoft.SourceLink.GitHub`), NuGet audit props, CI upload of `*.snupkg`
- Hardware test gate (`SONORA_RUN_HARDWARE_TESTS=1`) shared helper; Soak category placeholder excluded from CI
- Sample: **Sonora Test Player** (`samples/Sonora.TestPlayer`) — Vetra + Bootstrap Windows validation UI; local Vetra feed via `scripts/pack-vetra.ps1`; hardware-independent controller tests

### Changed

- Package version set to **`1.0.0-rc.1`** (not yet `1.0.0`)
- Limiter default `LookaheadMs = 5` guaranteeing sample-peak ceiling; documented contract (LA=0 measured ≥1 sample delay, not true-peak)
- Recommended DSP order **Preamp → EQ → Limiter**
- CI: `fetch-depth: 0`, test filter `Category!=Hardware&Category!=Soak`, vulnerability listing, package-consumer smoke
- `AudioPlayer.Volume` applies on the **output pull path** (after the PCM ring, before the effect rack) so loudness changes are not delayed by `BufferDuration`
- WASAPI exclusive end-of-stream: pad the final render period; stop WasapiPlayer off the render thread after EOS so `AUDCLNT_E_BUFFER_SIZE_ERROR` (0x88890016) is avoided; do not auto-recover exclusive init/format/busy failures in a tight device-loss loop

### Fixed

- Decode-pump ownership: stop wait timeouts no longer authorize `Seek` / restart / decoder dispose — worker completion is authoritative; `DisposePending` blocks new pump starts; deferred one-shot resource release after the worker exits; `DisposeAsync` awaits real release (not a bounded stop wait); failed seek with an incomplete pump stop exits the effects barrier and lands in `Stopped` (no Playing+silence limbo); `StopPump` runs off `_gate` with pipeline identity checks so a delayed stop cannot touch a newly promoted track
- Device-loss recovery / CI hang: `Stopped` handling runs on a dedicated thread (avoids thread-pool starvation from ownership joins); decode-pump `ShutdownCompleted` is signaled on the worker itself so `DisposeAsync` cannot stall behind a starved `ContinueWith`
- CI output-switch tests: superseded-create waits use async cancellation (not sync `Sleep`/`WaitOne` on the pool) and bounded awaits so parallel runs cannot blame-hang the testhost; `DisposeAsync_DuringConfigure` allows for exclusive rebind grace before create
- Seek / Stop AppHang: `SeekWithResult` and Stop/EOS decoder rewind no longer hold the command gate across `StopPump` / `IAudioDecoder.Seek` / ring prime (Media Foundation seeks could freeze Pause/Stop/Dispose and hang Test Player); Pause mid-seek is honored; Stop supersedes in-flight seek apply; decoder seeks serialized; Test Player bridge runs seek off the UI thread
- WASAPI / player lifecycle: concurrent `Stop` / `Shutdown` / `Dispose` await the same shared completion; recovery always shuts down detached outputs; EOS latch resets on Play; stale `Stopped` senders ignored; Pause honored during rebind; player Dispose/DisposeAsync share teardown including queued output shutdown
- Shared→Exclusive GetBuffer AV (`0xC0000005`): disconnect sample source before Stop/Join; brief COM settle before WasapiPlayer dispose; longer endpoint release/init graces; RCW collect assist; post-probe settle; retry exclusive create on recoverable failures; exclusive default latency 150ms
- Output switch Position jump: while `OutputDeviceStatus.Switching`, `Position` returns the held audible snapshot (seek/pause during the window update it); mark Switching before Detach and silence the pull path without draining the ring so a dying consumer cannot advance `samplesConsumed` or steal primed PCM; failed rebind no longer clobbers mid-switch seek position
- Seek hang / Playing+silent: do not `Pause` the output for the whole `Decoder.Seek` window (effects barrier silence only); freeze `Position` via `_seekInProgress`; abort superseded seeks before Media Foundation seek; serialize Test Player seeks; gapless `trackChanged` artwork uses payload id when snapshot is still stale
- Rapid Duration+10 / gapless seek hang: arm effects barrier under the pipeline lock before releasing the command gate (close promote window); never `StopPump`/MF-seek a promoted-away decoder; Seek no longer suppresses committed `TrackTransitioned` (artwork / queue index); coalesce Test Player seeks to the latest target (host + UI)
- Test Player seek panel dead after one use: UI no longer treats real decoder `Unknown` accuracy as a coalesced skip; host marks true coalesces with `coalesced: true`
- `AudioPlayer` / `AudioEngine` disposal: detach session under the command gate, complete output shutdown and decode-pump stop **off-gate** (eliminates sync-over-async wait while holding `_gate`); additive `IAsyncDisposable`
- Engine unregisters players on dispose (avoids double-dispose list growth)
- Limiter sanitizes non-finite PCM samples; default look-ahead raised to 5 ms after measurement (explicit `LookaheadMs=0` still available)
- WASAPI exclusive: wrong `AUDCLNT_E_BUFFER_SIZE_ERROR` HRESULT constant (`0x88890018` → `0x88890016`) caused end-of-stream to be misclassified as device failure; Shared→Exclusive with `ExclusiveModeFallback.None` could crash/hang Test Player via recovery storm
- WASAPI exclusive Pause: NAudio soft-pause left `IAudioClient` running so the last exclusive period repeated (~1 ms loop); Pause now keeps the client running but feeds silence from the PCM adapter (avoids Stop/GetBuffer races)
- WASAPI Shared→Exclusive: avoid exclusive open/close during format probe; brief endpoint-release grace after disposing the previous output (mitigates intermittent native `GetBuffer` AV `0xC0000005`)
- Exclusive Stop/gapless then reload: wait for async output dispose before reopening; initialize output before publishing `_output` (failed Init no longer leaves a disposed zombie that breaks Seek); preference-only configure releases orphaned clients; failed Load tears down half-attached pipelines (fixes `DEVICE_IN_USE` / `ObjectDisposedException` after Stop)
- PCM adapter: pad short periods without latching EOS on transient underruns; silence-after-EOS must not interfere with pause
- Test Player: serialize output configure commands; surface configure failures as nonfatal UI errors and resync effective mode after success or failure; clarify configure-before-load messaging when `EffectiveMode` is null by design
- Test Player Audio Output form: Apply enablement tracks dirty requested config (not effective mode); output snapshots no longer clobber unsaved Shared/Exclusive selections; clear `configureInFlight` as soon as configure returns (before refresh) and reconcile in-flight applies from matching output events so Exclusive→Shared Apply cannot stay stuck while playback continues
- Test Player: do not toast exclusive “no output attached” while `deviceStatus` is mid-rebind (`switching`)
- HTTP live radio (Icecast/SHOUTcast): reject fake `Content-Range: …/1073741823` as seekable; open forward-only without `Range: bytes=0-`; fall back to Media Foundation URL open for non-seekable MP3 mounts (fixes SomaFM-style loads timing out as `TaskCanceledException` / false `stream.cancelled`)
- WASAPI Shared→Exclusive after a device switch while playing: exclusive uses polling sync (not event sync), 100ms default exclusive latency, longer release/init settle, deferred exclusive `Play` (mitigates native `GetBuffer` AV `0xC0000005` process crash)
- WASAPI output teardown: on a UI sync context, run Stop/Join dispose on a dedicated thread (not `Task.Run().GetResult()`, which starved under parallel CI and caused AppHang on WinForms/Vetra)
- Output rebind: await Shutdown on a dedicated thread (not `ForceYield`/`Task.Run` on the pool) so Switching observers can proceed without starving under parallel held-shutdown CI; queue orphaned teardowns on a dedicated thread as well
- Disposal hardening tests: start `Dispose` on a dedicated thread when observing held Shutdown (avoids false CI failures when `Task.Run` cannot schedule under parallel pool saturation); `DisposeAsync` teardown awaits Shutdown instead of sync-over-async `GetResult`)
- Test Player: clear device-row “Switching” spinner when host settles to Available (and always re-render after `devices.set`)

### Notes

- SemVer applies for the 1.0 line; see `docs/versioning.md`. API freeze from 0.9 with additive `DisposeAsync`.
- Do not promote to `1.0.0` without `docs/production-checklist.md`.
- Known residual risks: Windows-only playback backend; MF seek Exact not claimed; HTTP SSRF is an application responsibility; exclusive mode not bit-perfect; performance baselines not yet measured; hardware suite must be opted in and actually run before claiming pass.

## 0.9.0 — 2026-11-01

### Added

- HTTP/HTTPS audio playback via `HttpAudioSource` and `AudioPlayer.LoadAsync(AudioSource)` / `PreloadNextAsync(AudioSource)`
- `FileAudioSource` preserves existing path-based `LoadAsync(string)` behavior (strings are never interpreted as URLs)
- Bounded HTTP transport in `Sonora.Audio` (`HttpStreamingOptions`, `HttpContentByteStream`, `HttpRandomAccessStream`) using built-in `HttpClient` — **no new NuGet packages**
- Range-verified seeking with strong ETag / `If-Range` validation; refuses to mix bytes from different resource versions
- `NetworkTransportStatus`, `NetworkBytesBuffered`, and `NetworkStatusChanged` (separate from `PlaybackState`)
- `NetworkAudioException` for transport failures
- `IAudioDecoder.HasKnownDuration` / `AudioPlayer.HasKnownDuration` to distinguish unknown duration from zero-length sources
- Stream-backed Windows decoders (`StreamMediaFoundationReader`, Wave/AIFF/Vorbis/Opus stream opens)
- Deterministic in-process HTTP tests (core) and fixture-backed HTTP format matrix (Windows)

### Changed

- Output format renegotiation reopens via stored `AudioSource` (not path-only); non-seekable HTTP at a non-zero position fails honestly when seamless continuity is impossible
- Audible `Position` freezes during underrun silence and network buffering/reconnect so WASAPI silence does not advance the timeline
- Decode-facing HTTP reads never return `0` on a temporary stall (`0` means genuine EOF only)

### HTTP format compatibility (verified with committed fixtures)

| Format | Range / seekable HTTP | Forward-only HTTP |
| --- | --- | --- |
| MP3 | Supported (MF) | Not claimed (MF requires seekable body) |
| FLAC | Supported (MF) | Not claimed |
| AAC (ADTS) | Supported (MF) | Not claimed |
| M4A | Supported when ranges reach indexes (MF) | Not claimed |
| WMA | Supported (MF) | Not claimed |
| WebM Opus | Supported when ranges reach cues (MF) | Not claimed |
| Ogg Vorbis | Supported | Supported (progressive) |
| Ogg Opus | Supported | Supported (progressive) |
| WAV | Supported when available as stream | Header-first progressive when reader allows |

### Notes

- Applications that accept arbitrary URLs should apply their own SSRF / network-egress controls
- Sensitive URL query parameters and `Authorization` headers are not logged; `Authorization` is stripped on cross-origin redirects
- Transport failure never raises `PlaybackCompleted`; network stalls never promote a gapless next track
- Live / non-range reconnect does not claim seamless continuity

## 0.8.0 — 2026-11-01

### Added

- Platform-neutral metadata API (`Sonora.Audio.Metadata`): `AudioTrackMetadata`, `AudioArtwork`, `IAudioMetadataReader`, `AudioMetadataReader`
- Opt-in embedded artwork via `AudioMetadataReadOptions.IncludeArtwork` (default `false` for library scans)
- Separate `ReadArtwork` path that avoids a full tag `Update` when ATL allows picture-focused reload
- `SonoraWindows.CreateMetadataReader()` convenience factory (same managed reader; no WASAPI)
- ReplayGain, MusicBrainz IDs, lyrics (unsynchronized plain text), compilation flag, and bounded `AdditionalTags`
- Artwork size/count limits; owned artwork byte copies valid after file disposal
- Non-hardware metadata tests and format coverage fixtures

### Changed

- `Sonora.Audio` depends on **z440.atl.core 7.19.0** (MIT) for tag extraction only; playback/decode paths remain independent. Transitive: `Ude.NetStandard` 1.2.0 (Mozilla Universal Charset Detector port; see package `COPYING`)
- On first metadata use, Sonora sets ATL `Settings.UseFileNameWhenNoTitle = false` once (ATL has no per-call settings isolation). Documented for apps that also use ATL directly

### Notes

- Metadata reading does not create an `AudioPlayer`, initialize WASAPI, or decode PCM for playback
- Multi-artist/genre: prefers format-native additional fields (e.g. Vorbis `ARTISTS`); does not blindly split on `;`
- ATL reports ALAC as lossy (`IsLossless == false`); WebM Opus may omit sample rate / channel count
- Duration is container-reported and not promised sample-accurate where encoder delay/padding exists

## 0.7.0 — 2026-11-01

### Added

- `AudioSeekResult` / `AudioSeekAccuracy` (`Exact`, `Approximate`, `Unknown`) with nullable `ActualPosition`
- `AudioPlayer.SeekWithResult(TimeSpan)` returning requested vs actual decoded landing
- Frame-authoritative `AudioFormat.TimeToFrames` / `FramesToTime` (tick arithmetic; round-trip safe)
- Bounded forward-discard after coarse seeks (WAV/AIFF/MF/Opus) without fabricating accuracy claims
- Deterministic seek accuracy tests (index-coded PCM, seek-to-zero during output switch, MF non-Exact policy)

### Changed

- **Breaking:** `IAudioDecoder.Seek` now returns `AudioSeekResult` (was `void`)
- `AudioPlayer.Seek` preserved as a void wrapper over `SeekWithResult`
- Timeline rebase after seek uses evidence-backed `ActualPosition` when present; otherwise requested-frame intent
- `WdlFormatConvertingDecoder` rebuilds the provider graph on seek (exception-safe; demotes Exact→Approximate when resampling)
- Seek quiesces the output pull path via an effects barrier so DSP `Reset` cannot race active callbacks (no WASAPI Shutdown per seek)

### Fixed

- Seek vs gapless promotion re-validates the active pipeline under `_pipelineSync`
- Seek-to-zero during an output-switch window is no longer overridden by a stale nonzero snapshot
- Failed seeks leave a consistent playback state instead of a half-cleared Playing session
- Seeking after EOS re-arms natural completion acceptance without raising a false `PlaybackCompleted`

### Notes

- `Position` remains an **audible estimate**; use `SeekWithResult` for requested/actual seek semantics.
- WAV/AIFF may report `Exact` when byte-position math proves the first retained frame.
- Media Foundation formats (FLAC, MP3, AAC/M4A, ALAC, WMA, WebM) and Ogg Opus do **not** claim `Exact` from container/`CurrentTime` timestamps; forward discard may improve proximity but returns `Unknown` (null `ActualPosition`) unless stronger evidence exists.
- `Unknown` never fabricates an actual position.
- Gapless next-track preservation and v0.5.0 output-quiescence guarantees are unchanged.

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
