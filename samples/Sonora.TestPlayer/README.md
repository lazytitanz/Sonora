# Sonora Test Player

Windows desktop validation harness for the Sonora audio engine. It exercises real public APIs under listening conditions that automated tests cannot fully cover: devices, exclusive mode, gapless joins, DSP, HTTP streaming, and long sessions.

This is **not** a general-purpose music player (and not EMP). Unsupported capabilities are labeled honestly; manual/hardware observations are never auto-marked as passed.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (Evergreen)
- Physical or virtual audio output device for listening tests (not required for unit tests)

## Obtain Vetra (local NuGet feed)

The UI host is [Vetra](https://github.com/lazytitanz/Vetra) **0.1.0**, which is **not** on NuGet.org. From the repository root:

```powershell
./scripts/pack-vetra.ps1
```

This clones the commit pinned in [`scripts/vetra-pin.txt`](../../scripts/vetra-pin.txt) into `deps/cache/vetra` and packs `Vetra.Core` / `Vetra.WinForms` into the repo-relative feed `deps/nuget/` (gitignored packages). [`nuget.config`](../../nuget.config) maps that folder as `SonoraLocalDeps`. Paths are never machine-specific.

Windows CI runs the same script before `dotnet restore`.

## Frontend assets (Bootstrap)

Bootstrap **5.3.8** and Bootstrap Icons **1.13.2** are bundled under `www/vendor/` (no CDN at runtime). Licenses are preserved next to the assets. To refresh from GitHub release zips:

```powershell
./scripts/sync-frontend-vendor.ps1
```

## Build and launch

```powershell
# From repository root (after pack-vetra.ps1 at least once)
dotnet restore Sonora.slnx
dotnet build samples/Sonora.TestPlayer/Sonora.TestPlayer.csproj -c Release
dotnet run --project samples/Sonora.TestPlayer/Sonora.TestPlayer.csproj -c Release --no-build
```

Startup failures (missing WebView2, missing `www`, Vetra init errors) show a **native message box** with exception details and close the window — the app will not sit on a blank form.

## What the sections cover

| Section | Purpose |
| --- | --- |
| Player | File queue (add-folder defaults to the Windows Music library, recursive, still changeable), transport, seek/`SeekWithResult` (async off the UI bridge so MF seeks cannot AppHang the host), gapless preload/transition, listening checklists |
| Audio Output | Device enumeration/selection, shared vs exclusive, fallback, latency request vs effective mode |
| DSP & Effects | Preamp → six-band EQ → limiter (Sonora rack order) |
| Streaming | `HttpAudioSource` only (not file `LoadAsync`), network status, restricted URL mode |
| Diagnostics | Engine snapshot, event log, process-level resources, export |
| Test Scenarios | Guided manual scenarios + soak session; results are human-recorded |

## Queue advancement

- Gapless handoffs advance the app queue from **`TrackTransitioned`**.
- When a track ends **without** a gapless transition and more queue items remain, **`PlaybackCompleted`** loads the next item.
- A coordinator dedupes stale generations and transition/completion races so the queue cannot double-advance.

Gapless joins are Sonora’s PCM hard-cut. Encoder-delay/padding correction and crossfade are **not** claimed.

## HTTP URL safety (important limitation)

Sonora’s HTTP transport does **not** expose DNS-resolution or redirect validation hooks to the application. The test player’s default mode performs **best-effort pre-flight checks** (literal loopback/private IPs, obvious local hostnames) and offers an explicit **developer allow-local** override with a visible warning.

That default mode is **not** a complete SSRF guarantee (DNS rebinding and redirects to private addresses can still occur inside the engine). Treat streaming URLs as a developer-controlled surface.

Untrusted URLs, metadata, paths, and errors are displayed with `textContent` (not as HTML). The WebView is never navigated to a stream URL.

## Metrics honesty

| Kind | Examples |
| --- | --- |
| Directly from Sonora | `State`, `Position`, `BufferedPosition`, `Duration`/`HasKnownDuration`, `Volume` (post-ring, immediate), next-track state, device/output mode, network status/bytes, underrun events, DSP gains |
| Application-observed | Event-log counts, underrun tallies in the store, scenario results, soak notes |
| Process-level (not Sonora alone) | Working set, managed heap, handle/thread counts, uptime |
| Unavailable / not fabricated | WASAPI callback timing, audio-thread CPU, output buffer occupancy, true-peak meters, clip counts, guaranteed audible transition timestamps |

UI polling intervals are **not** audio callback latency.

## Manual hardware tests

Use Audio Output and Test Scenarios for device unplug/replug, Windows default changes, exclusive busy devices, and soak listening. Record Pass / Fail / Inconclusive yourself. The app never auto-passes perception or hardware steps.

## Diagnostic export

Diagnostics → Export writes a local JSON (and optional text) report: versions, OS/runtime, output config, scenario results, events, errors, underrun counts, process snapshots, notes, and measurement limitations. Full paths / usernames / URL query strings are redacted unless you opt in to sensitive details.

## Gapless PCM capture (developer)

Diagnostics → **Gapless PCM** captures bounded float samples at the managed→WASAPI boundary when playback `Position` reaches a chosen offset on the current track. Use this to compare a **natural promote** of B with a **manual Load** of the same B while listening.

## Listening A/B test (developer)

Diagnostics → **Run Listening A/B Test** correlates **what you hear** with managed-boundary PCM from the same attempt.

1. Set comfortable volume (~17%). Keep DSP bypass if you want a clean A/B.
2. Enter Track A and Track B paths (or queue two files — first = A, second = B).
3. Optionally set the Gapless PCM output folder (defaults under `%USERPROFILE%\sonora-listening-ab`).
4. Click **Run Listening A/B Test**. The host will:
   - Load A, preload B to Ready, play through a verified `TrackTransitioned`, capture windows on B, then ask how it sounded.
   - Stop, fresh-Load B (no `TrackTransitioned`), capture the same windows, then ask again.
5. After each scenario, choose **Clean**, **Static / distorted**, or **Unsure**, then **Continue**.
6. Read the on-screen summary (e.g. `Natural promotion: Static / PCM exact`) and open `report.json` in the timestamped dump folder.

Listening class is never inferred from PCM. Matching managed PCM with an audible difference only suggests looking downstream of the capture boundary — it does not prove the cause.

Typical flow (scenario `gapless.pcm-boundary-diag`):

1. Bypass DSP; gapless on; queue short A then the file that distorts on automatic transition.
2. Set an output folder; click **Capture after promote**; Play and listen.
3. Stop; load only B; Play; **Capture manual Load**.
4. **Compare last**; paste the JSON and your listen notes into the scenario result.

Captures and compares never run on the WASAPI callback thread. Do not treat a PCM match alone as proof that distortion is downstream of the managed boundary.

## Automated tests

```powershell
dotnet test tests/Sonora.TestPlayer.Tests/Sonora.TestPlayer.Tests.csproj -c Release --filter "Category!=Hardware&Category!=Soak"
```

These tests cover URL pre-checks, diagnostic redaction, queue-advance coordination, and the event store. They do **not** launch the GUI or require WASAPI hardware. Standard CI builds the sample and runs the same filter; it does not start the windowed app.

## Soak session

1. Open Test Scenarios → start soak.
2. Play representative material for the desired duration.
3. Note audible issues when they occur; watch Diagnostics for errors/underruns and process resources.
4. Stop soak and export diagnostics.

Remaining open without errors is **not** automatically a pass — record the outcome manually after listening.
