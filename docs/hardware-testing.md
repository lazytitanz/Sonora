# Hardware testing

WASAPI and real-device tests are tagged `[Trait("Category", "Hardware")]` and are **excluded from default CI**.

## Opt-in

```bash
# Windows PowerShell
$env:SONORA_RUN_HARDWARE_TESTS = "1"
dotnet test tests/Sonora.Audio.Windows.Tests -c Release --filter "Category=Hardware"
```

Without `SONORA_RUN_HARDWARE_TESTS=1`, Hardware-trait tests **skip** via `[HardwareFact]` / `[HardwareTheory]` (`HardwareTestGate`) even if the Category filter includes them.

CI filter (non-hardware):

```text
Category!=Hardware&Category!=Soak
```

## Safety

- Keep **volume low** (tests typically use `0.05`–`0.2`).
- Prefer short fixtures (hundreds of ms).
- Do not run exclusive-mode tests on shared lab machines without warning others — exclusive mode can fail or fall back when the endpoint is busy.
- Exclusive mode is **not** bit-perfect when DSP, volume, or conversion are active; shared fallback may apply when configured.

## Manual checks (not automated)

- Hotplug / unplug the preferred render device while Playing / Paused.
- Switch Windows default device while following default (`SetOutputDeviceAsync(null)`).
- Confirm recovery intent vs explicit `Stop` / `LoadAsync` / `Dispose` clearing resume.

## Gapless 44.1 kHz upward SRC

Audible static when naturally promoting 44.1 kHz WaveStream-backed tracks (Vorbis/AAC/WAV) into a 48/96 kHz session was traced to `WaveStreamAudioDecoder` capping each `Read` at 1024 floats, which flushed NAudio WDL every DecodePump period. After the fill-loop fix, re-listen Opus 48 → Vorbis/AAC 44.1 with gapless on.

## Gapless session isolation (Experiment A)

After promote static that survives Seek / Stop→Play but clears on fresh Load, isolate whether rebuilding **only** the output session clears the noise while the promoted track continues.

**No new helpers.** Use TestPlayer → Audio Output:

1. Shared mode, gapless on, reproduce audible static on natural promote of B.
2. Change **only** Requested latency (keep mode/fallback). Apply configuration.
3. Confirm: new latency shown, brief Switching then Available, **same** track (no Load).
4. Classify: static cleared / persisted / changed.

Rebind also resets adapter/effects/buffering — a clear does **not** prove Windows/WASAPI itself is defective. Full lifecycle table, caveats, and result log: [`gapless-session-isolation-findings.md`](gapless-session-isolation-findings.md).

## Gapless managed-boundary PCM (real fixture)

Primary listening repro is **Sonora.TestPlayer** (Diagnostics → Gapless PCM). Automated hardware tests are secondary.

### TestPlayer (preferred)

1. Bypass DSP; enable gapless; queue a short A then B (`test-audio-wav-16bit-44khz.wav` or another file that distorts on natural promote).
2. Confirm the gapless panel shows next **Ready** (preload B) before A ends — otherwise TestPlayer advances via `PlaybackCompleted` + Load and there is no `TrackTransitioned`.
3. Diagnostics → Gapless PCM: set an output folder, start ~3000 ms, window ~500 ms.
4. Click **Capture after promote** while A is current (before the handoff), then Play if needed. Watch phase status (`waitingForTransition` → `transitionObserved` → …). Listen — note whether B is distorted.
5. Stop; Load only B; Play; **Capture manual Load**.
6. **Compare last**. Record listen class + compare JSON in scenario `gapless.pcm-boundary-diag`.
7. A PCM match without audible promote distortion is **not** proof the bug is post-boundary.

### Automated (optional)

```powershell
$env:SONORA_RUN_HARDWARE_TESTS = "1"
$env:SONORA_DIAG_GAPLESS_B = "C:\path\to\test-audio-wav-16bit-44khz.wav"  # local only; do not commit
# optional:
# $env:SONORA_DIAG_GAPLESS_A = "C:\path\to\short-a.wav"
# $env:SONORA_DIAG_LISTEN_RESULT = "promote_distorted_load_clean"  # or both_clean | both_distorted | inconclusive
# $env:SONORA_DIAG_CAPTURE_DIR = "$env:USERPROFILE\sonora-gapless-pcm"
# $env:SONORA_DIAG_CAPTURE_SKIP_MS = "3000"
# $env:SONORA_DIAG_CAPTURE_WINDOW_MS = "500"
dotnet test tests/Sonora.Audio.Windows.Tests -c Release --filter "FullyQualifiedName~WasapiManagedBoundaryDiagTests"
```

Captures arm when `AudioPlayer.Position` reaches the start time on B (equivalent timeline), then NCC-align. Do not commit third-party audio.

## WAV PCM integrity (reference fidelity)

Compares a local PCM16 WAV against an independent float32 reference at the decoder and managed→WASAPI boundary. Diagnostic only — does not change playback.

```powershell
$env:SONORA_DIAG_PCM_REF_WAV = "C:\path\to\test-audio-wav-16bit-44khz.wav"  # local only; do not commit
# Phase 2 (decoder, no hardware):
dotnet test tests/Sonora.Audio.Windows.Tests -c Release --filter "FullyQualifiedName~WavPcmIntegrityDiagTests.Phase2"

# Phase 3 (managed output; measurement volume 1.0 — not for listening):
$env:SONORA_RUN_HARDWARE_TESTS = "1"
dotnet test tests/Sonora.Audio.Windows.Tests -c Release --filter "FullyQualifiedName~WavPcmIntegrityDiagTests.Phase3"
```

Optional: `SONORA_DIAG_PCM_SKIP_MS`, `SONORA_DIAG_PCM_WINDOW_MS`. Falls back to `SONORA_DIAG_GAPLESS_B` when `SONORA_DIAG_PCM_REF_WAV` is unset.

## Gapless promote integrity (known-bad A→B)

Verifies real `TrackTransitioned` (not Load-after-EOS), dumps early+mid managed PCM for promote vs Load vs independent WAV reference. **Preserves every attempt** — no retry-until-match. Listen class is human-only.

```powershell
$env:SONORA_RUN_HARDWARE_TESTS = "1"
$env:SONORA_DIAG_GAPLESS_A = "C:\path\to\known-bad-A.wav"   # required for primary (no synthetic fallback)
$env:SONORA_DIAG_GAPLESS_B = "C:\path\to\known-bad-B.wav"   # or SONORA_DIAG_PCM_REF_WAV
$env:SONORA_DIAG_PROMOTE_LISTEN = "Static"  # Clean | Static | Unsure — from listening, not PCM
$env:SONORA_DIAG_CAPTURE_DIR = "$env:USERPROFILE\sonora-gapless-promote-integrity"
dotnet test tests/Sonora.Audio.Windows.Tests -c Release --filter "FullyQualifiedName~GaplessPromoteIntegrityDiagTests.KnownBadPair"
```

Synthetic-A isolation (separate Fact): `FullyQualifiedName~GaplessPromoteIntegrityDiagTests.SyntheticA`.

## Reporting results

**Do not claim hardware tests passed** unless they were actually executed with `SONORA_RUN_HARDWARE_TESTS=1` on a machine with a working render endpoint. Skipping or filtering Hardware out of CI is expected and is not a pass.
