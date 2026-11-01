# Codec portability

Claims below reflect Sonora’s verified behavior. “Supported” means a working, fixture- or matrix-backed path — not a marketing checklist.

**Platform note:** Linux CI builds and tests **`Sonora.Audio` (core) only**. Playback, WASAPI output, and Windows decoder backends live in **`Sonora.Audio.Windows`** and require Windows 10/11 today.

## Matrix

| Format | Local file | HTTP range / seek | HTTP forward-only | Metadata (ATL) | Seek accuracy notes | Platform | Limits |
| --- | --- | --- | --- | --- | --- | --- | --- |
| WAV (PCM) | Yes | Yes when streamable | Header-first progressive when reader allows | Yes | May report `Exact` when byte math verifies | Windows playback | PCM only |
| AIFF / AIF (PCM) | Yes | Same family as WAV when stream open works | Progressive when reader allows | Yes (partial artwork) | May report `Exact` when byte math verifies | Windows playback | PCM (NAudio.Core) |
| MP3 | Yes (MF) | Yes (MF) | **Not claimed** (MF needs seekable body) | Yes | Never `Exact` from MF timestamps alone | Windows playback | Host Media Foundation |
| FLAC | Yes (MF) | Yes (MF) | **Not claimed** | Yes | `Unknown` / `Approximate`; content-verified | Windows playback | Host MF |
| AAC (ADTS `.aac`) | Yes (MF) | Yes (MF) | **Not claimed** | Via container/tags when present | Never `Exact` from MF alone | Windows playback | Host MF |
| M4A (AAC) | Yes (MF) | Yes when ranges reach indexes | **Not claimed** | Yes | Never `Exact` from MF alone | Windows playback | Host MF |
| M4A (ALAC) | Yes (MF) | Same as M4A when indexes reachable | **Not claimed** | Yes; ATL may report lossy | Never `Exact` from MF alone | Windows playback (Win10+) | Host MF |
| WMA | Yes (MF) | Yes (MF) | **Not claimed** | Yes | Never `Exact` from MF alone | Windows playback | Host MF / ASF |
| Ogg Vorbis | Yes (NVorbis) | Yes | Yes (progressive) | Yes | Managed; not MF Exact | Windows playback | MIT managed stack |
| Ogg Opus (`.opus`) | Yes (Concentus) | Yes | Yes (progressive) | Yes | Managed; not MF Exact | Windows playback | Bare `.opus` not via MF |
| WebM Opus | Yes (MF) | Yes when ranges reach cues | **Not claimed** | Partial; rate/channels often omitted | Never `Exact` from MF alone | Windows playback | Host MF; WebM Vorbis **not** claimed |

## Summary rules

1. **Core vs playback:** Decoding contracts and metadata APIs can be exercised on Linux for core unit tests; opening WASAPI and Windows codecs requires the Windows backend.
2. **HTTP:** Range-verified seeking uses strong validators; forward-only MF formats are not claimed. Applications must apply their own SSRF / egress controls ([SECURITY.md](../SECURITY.md)).
3. **Seek:** `AudioSeekAccuracy.Exact` is reserved for proven landings (typically WAV/AIFF). Media Foundation formats do not claim Exact from container timestamps alone.
4. **Metadata:** Read-only via ATL.NET; does not invent titles from file names; artwork is owned copies when requested.

See README format tables and [CHANGELOG](../CHANGELOG.md) HTTP sections for release-specific verification notes.
