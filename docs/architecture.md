# Sonora architecture

## Layers

```text
Application (console, WPF, WinForms, services)
        │
        ▼
Sonora.Audio.Windows          ← NAudio, WASAPI, MF, NVorbis, Concentus
        │ implements
        ▼
Sonora.Audio                  ← public API, player, ring buffer, pump
```

`Sonora.Audio` never references NAudio or Windows-specific assemblies. Platform backends implement `IAudioDecoderFactory` and `IAudioOutputFactory`. Managed Vorbis/Opus packages stay in `Sonora.Audio.Windows` only.

## Playback pipeline

1. `AudioPlayer.LoadAsync` opens an `IAudioDecoder` (may perform disk/codec I/O).
2. A **decode pump** (background worker) reads float32 PCM, runs optional `IAudioProcessor`s and volume, and writes into a **bounded ring buffer**.
3. WASAPI / `IAudioOutput` callbacks pull only from the ring via `IPcmSampleSource` — **no decoding or file I/O on the audio callback**.
4. Underruns fill silence and raise an edge-triggered `Underrun` event.

## Concurrency

| Concern | Strategy |
|---|---|
| Play / Pause / Stop / Seek / Load / Dispose | Serialized with `SemaphoreSlim(1,1)` |
| Overlapping loads | Generation counter + cancellation; stale work discarded |
| Ring buffer | Short critical section; consumer path is non-blocking |
| Output callback | Never waits on the command gate; never opens files |
| Events | May fire under the command gate; re-entering player commands from handlers is unsupported |

Ordered teardown: stop output → stop pump → dispose decoder/output.

## Position model

- `BufferedPosition` — decode frontier (samples written to the ring).
- `Position` — estimated audible time. On Windows, `WasapiPlayer.GetPosition()` is rebased at load/seek/stop so the logical timeline remains consistent.

Accuracy limits are documented in the README.

## Windows backend (verified)

| Item | Choice |
|---|---|
| Package | NAudio **3.3.0** (`net9.0+`) |
| Output | `WasapiPlayer` / `WasapiPlayerBuilder` shared mode |
| Selection | Internal magic-byte / container sniff → backend (`DecoderSelector`) |
| WAV | `WaveFileReader` |
| AIFF | `AiffFileReader` |
| MP3 / ADTS AAC / M4A / ALAC / WMA / FLAC / WebM | `MediaFoundationReader` with `RepositionInRead=false`, `RequestFloatOutput=true` |
| Ogg Vorbis | `VorbisWaveReader` (NAudio.Vorbis / NVorbis) |
| Ogg Opus | Concentus + Concentus.Oggfile (`OpusOggAudioDecoder`) |
| MF seek | Immediate `CurrentTime` seek; recreate reader only as fallback after EOF/invalid state |

```text
LoadAsync → WindowsAudioDecoderFactory → DecoderSelector (sniff)
  → Wave / AIFF / MF / Vorbis / Opus → IAudioDecoder → decode pump
```

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
