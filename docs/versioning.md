# Versioning

Sonora follows [Semantic Versioning](https://semver.org/) for public packages `Sonora.Audio` and `Sonora.Audio.Windows`.

## After 1.0.0

Once `1.0.0` ships:

| Change kind | Version bump | Examples |
| --- | --- | --- |
| Breaking public API / behavior | **MAJOR** | Removing members, changing required semantics |
| Backward-compatible features | **MINOR** | New types/methods, additive overloads |
| Bug fixes / docs / packaging | **PATCH** | Correctness fixes that preserve API |

Pre-1.0 releases (`0.x`) allowed deliberate breaking changes; those rules no longer apply after 1.0.

## Release candidates

Release candidates use the SemVer pre-release label `rc.N`:

- `1.0.0-rc.1`, `1.0.0-rc.2`, …
- RCs are **publishable** NuGet versions for validation; they are not the final `1.0.0` gate.
- Promoting `rc.N` → `1.0.0` requires the [production checklist](production-checklist.md).
- Do not publish `1.0.0` automatically from CI without an explicit human approval.

## API freeze from 0.9

The public surface of `Sonora.Audio` was treated as frozen for the 1.0 RC line starting from **0.9.0**, with one intentional additive change:

- **`DisposeAsync`** on `AudioEngine` / `AudioPlayer` (and related async disposal paths) — additive; synchronous `Dispose` remains.

No other breaking public API changes are expected between the 0.9 freeze and `1.0.0`. Further additive APIs may appear in post-1.0 minor releases.

Public API snapshots live under:

- `tests/Sonora.Audio.Tests/Baselines/Sonora.Audio.PublicApi.txt` — **v1.0 RC baseline**
- `tests/Sonora.Audio.Tests/Baselines/Sonora.Audio.PublicApi.v0.9.txt` — same surface with a header noting `DisposeAsync` as the additive delta from the freeze

See also [CHANGELOG.md](../CHANGELOG.md).
