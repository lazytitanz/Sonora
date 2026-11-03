# Sonora architecture

## Layers

```text
Application (console, WPF, WinForms, services)
        │
        ├─ metadata (optional) ──► Sonora.Audio.Metadata ← ATL.NET (z440.atl.core)
        │
        ▼
Sonora.Audio.Windows          ← NAudio, WASAPI, MF, NVorbis, Concentus
    │ implements
        ▼
Sonora.Audio                  ← public API, player, ring buffer, pump
```

`Sonora.Audio` never references NAudio or Windows-specific assemblies. Platform backends implement `IAudioDecoderFactory` and `IAudioOutputFactory`. Managed Vorbis/Opus packages stay in `Sonora.Audio.Windows` only.

Metadata (`IAudioMetadataReader` / `AudioMetadataReader`) lives in `Sonora.Audio` and uses ATL.NET for tag parsing. It does not touch the decode pump or WASAPI. Playback code paths do not call the metadata reader.

## Playback pipeline

1. `AudioPlayer.LoadAsync` opens an `IAudioDecoder` from a file path or `AudioSource` (may perform disk/network/codec I/O).
2. HTTP sources use a bounded byte transport in `Sonora.Audio` (`HttpRandomAccessStream` for verified ranges, `HttpContentByteStream` for forward-only). Network stalls block the **decode worker** only and never return `0` (EOF) until the body completes.
3. A **decode pump** (background worker) reads float32 PCM, runs optional `IAudioProcessor`s, and writes into a **bounded ring buffer**.
4. WASAPI / `IAudioOutput` callbacks pull from the ring via `IPcmSampleSource`, then apply master `Volume` and the effect rack — **no decoding, file, or HTTP I/O on the audio callback**. Volume and live DSP therefore do not wait for the ring to drain.
5. Underruns fill silence and raise an edge-triggered `Underrun` event; audible `Position` freezes during underrun silence and network buffering so device clocks do not invent timeline progress.
6. `NetworkTransportStatus` is independent of `PlaybackState` (Playing/Paused intent is preserved while buffering/reconnecting).

## Concurrency

| Concern | Strategy |
|---|---|
| Play / Pause / Stop / Seek / Load / Dispose | Command phases serialized with `SemaphoreSlim(1,1)` (`_gate`) |
| Decoder seek / pump stop / prime | Run **off** `_gate` (and serialized with `_decoderSeekGate`) so a slow Media Foundation seek cannot freeze Pause/Stop/Dispose |
| Decode-worker lifetime | **Worker completion** is authoritative for Seek / restart / decoder dispose; a stop-wait timeout only bounds the caller — the pipeline still owns a live worker (`HasOrphanDecodeWorker` is diagnostic) |
| Dispose / DisposeAsync | Entering dispose is terminal (`DisposePending`: no new Seek/StartPump); decoder/ring release runs once after the worker exits; `DisposeAsync` awaits that release (timeout ≠ disposed) |
| Stale pump stop | Off-gate `StopPump` completions check pipeline identity / generation so a delayed join cannot affect a newly promoted track |
| Overlapping loads | Generation counter + cancellation; stale work discarded |
| Ring buffer | Short critical section; consumer path is non-blocking |
| Output callback | Never waits on the command gate; never opens files; never runs dispose/cleanup continuations |
| Output `Stopped` | Marshaled on a **dedicated thread** (not `Task.Run`) so device-loss recovery is not starved by blocked pool threads |
| Events | May fire under the command gate; re-entering player commands from handlers is unsupported |
| Output Stop / Shutdown / Dispose | Concurrent callers await the **same** shared completion (not merely skip duplicate work) |

Ordered teardown: stop output → request pump cancel under `_gate` → join pump **off** `_gate` → dispose decoder/ring only after worker exit. A native `Decoder.Read` that never returns cannot be force-killed; ownership holds the decoder until the worker exits.

## Position model

- `BufferedPosition` — decode frontier (samples written to the ring).
- `Position` — estimated audible time. On Windows, `WasapiPlayer.GetPosition()` is rebased at load/seek/stop so the logical timeline remains consistent. While `OutputDeviceStatus.Switching`, reports the held audible snapshot (seek/pause may update it) so UI does not observe sample-based jumps while the previous output is detached; the pull path also feeds silence without draining the ring during that window.
- `SeekWithResult` — requested vs actual source-frame landing (`AudioSeekResult`); `Unknown` ⇒ null `ActualPosition`.
- Frame indices are authoritative inside converters (`TimeToFrames` / `FramesToTime`); `TimeSpan` is used at API boundaries.

Seek path (command gate released around long work):

1. Under `_gate`: pause output, enter effects barrier, bump seek generation.
2. Off `_gate`: stop pump and **wait for worker exit** → clear ring → `IAudioDecoder.Seek` (may block on Media Foundation) → optional ring prime. If the worker has not exited after the caller wait bound, seek fails, the effects barrier is released, and playback lands in `Stopped` (not Playing+silence).
3. Under `_gate`: reset DSP, rebase timeline, exit barrier; resume only if still playing (Pause mid-seek is honored; Stop supersedes via generation).

`Stop` / natural EOS rewind the decoder to zero the same way (off `_gate`, only after worker join + pipeline identity check). Does not `Shutdown` WASAPI per seek.

Accuracy limits are documented in the README.

## Windows backend (verified)

| Item | Choice |
|---|---|
| Package | NAudio **3.3.0** (`net9.0+`) |
| Output | `WasapiPlayer` / `WasapiPlayerBuilder` shared (default) or exclusive |
| Selection | Internal magic-byte / container sniff → backend (`DecoderSelector`) |
| WAV | `WaveFileReader` |
| AIFF | `AiffFileReader` |
| MP3 / ADTS AAC / M4A / ALAC / WMA / FLAC / WebM | `MediaFoundationReader` with `RepositionInRead=false`, `RequestFloatOutput=true` |
| Ogg Vorbis | `VorbisWaveReader` (NAudio.Vorbis / NVorbis) |
| Ogg Opus | Concentus + Concentus.Oggfile (`OpusOggAudioDecoder`) |
| MF seek | Coarse `CurrentTime` seek + bounded forward discard; recreate after EOF/invalid state; never Exact from MF timestamps alone |
| WAV/AIFF seek | Frame-aligned byte `Position`; may report `Exact` |
| Opus seek | Page-granular `SeekTo` + preroll discard; `Unknown` accuracy |

```text
LoadAsync(AudioSource)
  → WindowsAudioDecoderFactory
      File → DecoderSelector (path sniff)
      HTTP → HttpRandomAccessStream (206) or HttpContentByteStream (forward-only)
           → DecoderSelector (stream sniff)
  → Wave / AIFF / MF / Vorbis / Opus → IAudioDecoder → decode pump
```

HTTP resource identity uses strong ETags with `If-Range` when available. Weak or missing validators are documented as less safe; Sonora refuses to stitch bytes across changed lengths/ETags.

## Output devices and modes (0.5.0 / 0.6.0)

```text
IAudioOutputDevices (Windows: MMDeviceEnumerator + notification client)
        │
        ▼
AudioPlayer.SetOutputDeviceAsync / ConfigureOutputAsync / recovery
        │ probe (exclusive, non-destructive) → detach → Shutdown → create → Init
        ▼
IAudioOutputFactory.CreateOutputAsync(AudioOutputCreateRequest)
        │
        ▼
WasapiAudioOutput (shared WithSharedMode or exclusive WithExclusiveMode)
```

| Concern | Behavior |
|---|---|
| Default role | Windows **Multimedia** render endpoint |
| Default mode | **Shared**; exclusive only via `ConfigureOutputAsync` |
| Follow default | Notification-driven rebind when the Multimedia default id changes (not OS stream routing) |
| Explicit select | `WithDevice(id)`; ignore unrelated default changes |
| Preferred lost | Fall back to Multimedia default; keep preferred id; no auto-restore |
| Exclusive probe | Format capability query without attaching a second PCM consumer |
| Exclusive fallback | Optional shared after recoverable failure; only after exclusive quiesce/dispose |
| Strict exclusive fail | `Unavailable` + preserve track / position intent / DSP / next for retry |
| Recovery intent | Separate from `PlaybackState`; cleared by `Stop` / `LoadAsync` / `Dispose` |
| Rebind | Detach under gate → `Shutdown` join off-gate → attach with generation check |
| Shutdown | Old output must not read `TransitionSampleSource` after successful `Shutdown`; failure → Unavailable, no replacement; concurrent Stop/Shutdown/Dispose share one completion |
| Status | `Switching` while detached mid-rebind; independent of `PlaybackState` |
| Superseded | Concurrent Stop/Load/Dispose/newer switch cancels in-flight switch (`OperationCanceledException`) |
| Clock / PCM | Ring preserved when format unchanged; old endpoint padding skipped (~`CurrentLatency`); brief gap expected |
| Sync | Switch generation + CTS; create runs **off** the command gate; stale outputs disposed |
| Events | Device and configuration events off native IMM / WASAPI threads and off `_gate` for mode changes |

## Gapless (0.4.0)

```text
Active Decoder (+ convert) → Pump → Ring A ─┐
                                           ├→ TransitionSampleSource → EffectRack → WASAPI
Next Decoder (+ convert) → Pump → Ring B ───┘
```

| Concern | Behavior |
|---|---|
| API | `PreloadNextAsync` / `ClearNext` / `NextTrackState` / `TrackTransitioned` |
| Output | One WASAPI session for the continuous play-through; format locked at `LoadAsync` |
| Conversion | Decode-pump only; Windows uses NAudio WDL (`WindowsAudioFormatConverter`) |
| Transition | Hard cut; single output `Read` may span A\|B; no silence/dup/drop at the join |
| Events | `TrackTransitioned` deferred off the audio callback; `PlaybackCompleted` only when no ready next |
| Seek | Preserves a safe next track; does not promote or notify |
| DSP | Same rack across joins; no `ResetProcessingState` on gapless handoff |

## DSP (0.3.0+)

```text
Decoder → [optional IAudioProcessor] → Volume → PcmRingBuffer (dry)
  → output pull → player EffectRack (preamp / EQ / limiter) → WASAPI
```

| Concern | Behavior |
|---|---|
| Location | Post-ring pull path for live EQ/preamp response |
| Public API | `AudioPlayer.Effects` (`IAudioEffectRack`) — NAudio-free |
| Windows | `WindowsAudioEffectFactory` wraps NAudio `Equalizer`, `GainEffect`, `LimiterEffect` |
| Chain edits | Immutable snapshots; removed effects disposed only when the callback cannot reference them |
| Parameter updates | Queued on the control thread; applied on the audio thread via `ApplyPendingUpdates` (includes `Equalizer.Update()`) |
| Track changes | Effect instances/settings preserved; `Configure` + `Reset` on load/seek/stop (not on gapless handoff) |
| PCM format | Interleaved float32 only (`WaveFormat.CreateIeeeFloatWaveFormat`) |
| Legacy | `AudioEngineOptions.Processors` still runs on the decode pump |

## DSP extension (legacy)

`IAudioProcessor` remains available on the pump path for non-interactive processing. Interactive EQ/preamp/limiter use `player.Effects`.

## Metadata (0.8.0)

```text
AudioMetadataReader.Read(path, options?)
        │
        ▼
ATL Track (tags; pictures only if IncludeArtwork / ReadArtwork)
        │
        ▼
AudioTrackMetadata (immutable Sonora models; ATL types never escape)
```

| Concern | Behavior |
|---|---|
| Dependency | `z440.atl.core` **7.19.0** (MIT) on `Sonora.Audio` |
| Settings | ATL has no isolated per-call config. Sonora sets `UseFileNameWhenNoTitle = false` once on first use; other Settings left alone. Empty strings / non-positive numbers normalized to null in Sonora |
| Artwork | Default path does not access `EmbeddedPictures`. `ReadArtwork` uses `Track(path, load: false)` then picture reload |
| Thread-safety | Concurrent independent reads on one reader instance are safe; no retained handles |
| Errors | Missing file → `FileNotFoundException`; unsupported → `UnsupportedFormatException`; corrupt → `InvalidAudioException`; tagless valid file → empty fields |
