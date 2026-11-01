# Performance baseline

Measured on a developer workstation for **`1.0.0-rc.1`**. These are host-local microbenchmarks (not CI gates). Re-run `benchmarks/Sonora.Benchmarks` after material pipeline changes.

## Environment

| Field | Value |
| --- | --- |
| Date | 2026-11-01 |
| Machine | NODE |
| OS | Windows NT 10.0.26200.0 |
| Processors | 16 |
| .NET runtime | 10.0.12 |
| Sonora version | `1.0.0-rc.1` |
| Configuration | Release |
| Harness | `benchmarks/Sonora.Benchmarks` |
| Block size | 256 frames, 48 kHz stereo (float32) |

## Results

| Workload | Iterations | Throughput | Allocations (approx.) |
| --- | --- | --- | --- |
| `PcmRingBuffer` write+`TryRead` | 50,000 | ~721,000 /s | ~0 B/iter |
| DSP rack `Process` (preamp → EQ → limiter) | 100,000 | ~62,800 blocks/s | ~0 B/iter |
| Limiter `Process` alone | 200,000 | ~308,000 blocks/s | ~0 B/iter |
| `AudioMetadataReader.Read` (tiny silent WAV) | 200 | ~8,800 /s | ~82 KB/iter (ATL open path) |

Notes:

- Steady-state DSP and ring paths showed negligible per-iteration allocation under `GC.GetAllocatedBytesForCurrentThread` after warmup.
- Metadata reads allocate by design (tag parsing / string materialization); not on the WASAPI callback path.
- WASAPI callback latency / underrun soak / exclusive-mode timings are **not** included here (require hardware + Soak runs).

## Method

```bash
dotnet run --project benchmarks/Sonora.Benchmarks -c Release
```

Identify bottlenecks before optimizing; re-measure after each change.
