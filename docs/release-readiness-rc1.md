# Sonora 1.0.0-rc.1 — Release readiness report

This report separates **automated validation**, **physical hardware validation**, and **untested / residual** scenarios. Do not promote to `1.0.0` until [`production-checklist.md`](production-checklist.md) is satisfied.

## Automated validation (executed)

| Gate | Result |
| --- | --- |
| Release build (`Sonora.slnx`) | Pass |
| Core unit tests (`Category!=Soak`) | **133 passed** (includes disposal / concurrent lifecycle / API snapshot) |
| Windows unit tests (`Category!=Hardware&Category!=Soak`) | **99 passed** (includes limiter contract / API snapshot) |
| Package pack `1.0.0-rc.1` nupkg + snupkg | Present in CI workflow |
| Package-consumer smoke (`tests/Sonora.PackageConsumer`) | Wired in CI (Windows full; Linux core-only) |
| Public API baselines committed | `Baselines/Sonora.Audio.PublicApi.txt`, `.v0.9.txt`, Windows baseline |
| Limiter sample-peak contract tests | Pass (measured LA=0 and LA≥1 hold ceiling for impulses/steps/bursts; default LA=5) |
| Dispose off-gate teardown tests | Pass |
| Microbenchmarks (`benchmarks/Sonora.Benchmarks`) | Measured — see [`performance-baseline.md`](performance-baseline.md) |
| NuGet audit / vulnerable package list | Enabled in CI |

## Physical hardware validation

| Item | Status |
| --- | --- |
| Shared-mode smoke / seek / Load-Stop cycles | **Not executed** in this RC preparation (opt-in via `SONORA_RUN_HARDWARE_TESTS=1`) |
| Exclusive-mode negotiate / fallback | **Not executed** |
| Device hotplug / default-device change | **Not executed** (manual — see [`hardware-testing.md`](hardware-testing.md)) |
| Long-running soak (hours) | **Not executed** (`Category=Soak` placeholder only) |

Do **not** claim hardware or soak validation passed for `1.0.0-rc.1`.

## Untested / residual risks

- Exclusive WASAPI busy / unsupported-format behavior on arbitrary devices.
- Multi-hour memory/handle growth under continuous playback.
- True-peak / inter-sample overs (explicitly out of contract).
- Media Foundation codec availability variance across Windows SKUs (especially WebM).
- Sync `Dispose` still blocks briefly off-gate during output quiescence / pump stop (no longer waits under `_gate`; UI apps should prefer `DisposeAsync`, which waits for decode-worker exit and resource release).
- Seek/Stop decoder work no longer holds `_gate`, but the calling thread can still block on Media Foundation seek or an incomplete pump join; UI hosts should keep seek off the UI thread (Test Player does). A stuck native `Decoder.Read` keeps the pipeline owning that decoder until the worker returns.
- HTTP SSRF / egress policy remains an application responsibility.
- Linux: `Sonora.Audio` builds and unit-tests; **playback still requires a platform backend** (Windows today).
- Performance numbers are one host’s microbenchmarks — not WASAPI callback p99.

## Promotion to 1.0.0

Follow [`production-checklist.md`](production-checklist.md). Minimum additions beyond this RC:

1. Recorded hardware smoke results on a real Windows device.
2. Windows + Linux CI green on the RC commit.
3. Any P0 bugs found during RC soak fixed and re-validated.
4. Bump `Directory.Build.props` Version from `1.0.0-rc.1` → `1.0.0` and finalize CHANGELOG.
