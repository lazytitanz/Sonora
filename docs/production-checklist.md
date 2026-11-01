# Production checklist — promoting `1.0.0-rc.1` → `1.0.0`

Do not publish `1.0.0` until every gate below is consciously signed off.

## Packaging / CI

- [ ] `Directory.Build.props` Version is `1.0.0` (not an RC label)
- [ ] CI green on Windows (build, non-hardware/non-soak tests, pack) and Linux core
- [ ] `*.nupkg` and `*.snupkg` artifacts produced; SourceLink resolves against the release tag/commit
- [ ] Package consumer smoke restores from packed artifacts (Windows: Audio + Windows; Linux: Audio only)
- [ ] `dotnet list package --vulnerable --include-transitive` reports no accepted-unfixed vulnerabilities (or documented exceptions)
- [ ] NuGet package metadata, README, license, and symbols verified locally

## API / docs

- [ ] Public API snapshots still match (or intentional diffs reviewed)
- [ ] CHANGELOG has a `1.0.0` section with SemVer-facing notes
- [ ] README version badge / install guidance updated off RC
- [ ] [versioning.md](versioning.md), [codec-portability.md](codec-portability.md), [limiter.md](limiter.md), SECURITY, THIRD-PARTY-NOTICES reviewed

## Correctness

- [ ] Dispose / DisposeAsync overlap and gate tests green
- [ ] Limiter contract tests green (default LookaheadMs guaranteeing sample-peak)
- [ ] HTTP matrix claims still match implemented behavior
- [ ] No known data-loss or stuck-Playing defects without documented workarounds

## Hardware / performance (manual)

- [ ] Hardware suite run with `SONORA_RUN_HARDWARE_TESTS=1` on at least one Win10/11 host ([hardware-testing.md](hardware-testing.md))
- [ ] Shared-mode Load/Stop cycle and exclusive fallback spot-checked at low volume
- [ ] [performance-baseline.md](performance-baseline.md) either measured or explicitly accepted as “NOT YET MEASURED” with owner follow-up

## Residual risk acceptance

- [ ] Known residual risks in the RC CHANGELOG still accurate and accepted for GA
- [ ] Explicit human approval to publish `1.0.0` to NuGet.org (no auto-publish)
