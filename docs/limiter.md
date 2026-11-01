# Limiter contract

Sonora’s limiter is a **sample-peak** look-ahead brick-wall used as clipping protection. It is **not** a true-peak / inter-sample / oversampled limiter. Soft clipping is not applied. Channels are **gain-linked** (one gain for all channels).

## Defaults

| Option | Default | Meaning |
| --- | --- | --- |
| `CeilingDb` | `0` | Linear ceiling 1.0 (0 dBFS) |
| `ReleaseMs` | `100` | Release time |
| `LookaheadMs` | **`5`** (`LimiterOptions.DefaultGuaranteeingLookaheadMs`) | Guaranteeing construction setting for abrupt sample-peak steps in Sonora’s tests |

`player.Effects.AddLimiter()` uses `new LimiterOptions()` unless options are supplied.

## Look-ahead semantics

| Setting | Contract |
| --- | --- |
| **Default `LookaheadMs = 5`** | Guarantees sample peaks at or below the linear ceiling for abrupt steps / impulses under Sonora’s measured tests (float tolerance). |
| **Explicit `LookaheadMs = 0`** | Minimum-latency setting. Measured with NAudio 3.3.0 (`TruePeak=false`): still applies **≥1 sample** of internal delay and held the sample-peak ceiling for impulses, mid-buffer steps, and sparse bursts in those tests — **not** a true-peak guarantee and may overshoot on some sudden rises depending on content. |

True-peak / inter-sample peaks are **never** claimed.

## Recommended rack order

**Preamp → Equalizer → Limiter**

Place the limiter last so boosted EQ/preamp peaks are constrained before WASAPI. Prefer sensible preamp/EQ gains rather than relying on the limiter alone.

## NaN / non-finite sanitization

The Windows limiter backend zeroes non-finite samples (`NaN` / `∞`) before and after the NAudio process call so poisoned values cannot stick in the look-ahead envelope or downstream PCM.

## Reset / gapless

Dynamics / filter state resets on seek, stop, load, and output reconfiguration. **Gapless track joins do not reset** limiter (or other effect) processing state — settings persist and the envelope continues across the hard-cut.

## Verification

See `tests/Sonora.Audio.Windows.Tests/LimiterContractTests.cs` for measured Process assertions. Do not weaken those tests to match aspirational docs.
