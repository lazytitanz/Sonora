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

## Reporting results

**Do not claim hardware tests passed** unless they were actually executed with `SONORA_RUN_HARDWARE_TESTS=1` on a machine with a working render endpoint. Skipping or filtering Hardware out of CI is expected and is not a pass.
