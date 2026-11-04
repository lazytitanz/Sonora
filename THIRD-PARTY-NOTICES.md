# Third-party notices

Sonora is MIT-licensed. It depends on the following third-party components (direct or relevant transitive). License texts ship with the respective NuGet packages; this file summarizes attribution.

## z440.atl.core (ATL.NET)

- **Use:** Tag / metadata extraction in `Sonora.Audio` (`AudioMetadataReader`)
- **License:** MIT
- **Project:** https://github.com/Zeugma440/atldotnet
- ATL types are **not** part of Sonora’s public API.

## Ude.NetStandard

- **Use:** Transitive charset detection dependency of ATL.NET
- **License:** Mozilla Universal Charset Detector port (see package `COPYING` / NuGet license)
- **Notes:** Brought in transitively via `z440.atl.core`; not referenced directly by Sonora.

## NAudio

- **Use:** WASAPI output, Media Foundation readers, Wave/AIFF readers, DSP primitives, format conversion helpers in `Sonora.Audio.Windows`
- **License:** MIT
- **Project:** https://github.com/naudio/NAudio
- NAudio types are **not** exposed through Sonora’s platform-independent public contracts.

## NVorbis / NAudio.Vorbis

- **Use:** Ogg Vorbis decode on Windows
- **License:** MIT
- **Projects:** https://github.com/NVorbis/NVorbis · NAudio.Vorbis package

## Concentus / Concentus.Oggfile

- **Use:** Ogg Opus decode on Windows (managed; bare `.opus` is not claimed via Media Foundation)
- **License:** MIT
- **Projects:** Concentus / Concentus.Oggfile NuGet packages

## ManagedCodecs.Containers / ManagedCodecs.Aac / ManagedCodecs.Core

- **Use:** Fragmented MPEG-4 (fMP4 / DASH-style) AAC demux and decode on Windows when Media Foundation produces no PCM; also used when container sniff detects `moof`/`mvex` or fMP4 brands
- **License:** MIT (packages ship their own `THIRD-PARTY-NOTICES.md` for transcribed codec constant tables)
- **Project:** https://github.com/bisforboman/ManagedCodecs
- ManagedCodecs types are **not** exposed through Sonora’s public API

---

Sonora itself: see [LICENSE](LICENSE).
