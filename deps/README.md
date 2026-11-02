# Local dependencies

This folder holds **generated** inputs for samples that cannot be restored from NuGet.org alone.

| Path | Purpose | Git |
| --- | --- | --- |
| `nuget/` | Local NuGet feed for Vetra packages | Directory tracked; `*.nupkg` gitignored |
| `cache/` | Clone/download scratch used by scripts | Fully gitignored |

## Vetra (Sonora Test Player)

Vetra is not published to NuGet.org. Pin and pack it before restoring the solution on Windows:

```powershell
./scripts/pack-vetra.ps1
dotnet restore Sonora.slnx
```

The pin is [`scripts/vetra-pin.txt`](../scripts/vetra-pin.txt). Windows CI runs the same script before restore.

## Frontend vendor assets

Bootstrap and Bootstrap Icons are committed under `samples/Sonora.TestPlayer/www/vendor/`. To refresh them:

```powershell
./scripts/sync-frontend-vendor.ps1
```
