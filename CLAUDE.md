# pCUE — handover (canonical)

Fan-control desktop app for the **Corsair Commander PRO** (Cybenetics LTD). WPF, **.NET Framework
4.8**, C# (classic `packages.config` project, AnyCPU). This file is the canonical handover; keep it
current. `AGENTS.md` is a thin pointer to this file.

## What it does
Reads the Commander PRO over USB-HID and shows per-fan RPM (Current/Min/Max/Avg) plus CPU
Temp/MHz/Load; lets the user set fan mode (Auto / 3-pin / 4-pin / Disconnect), fan speed
(PWM % or fixed RPM), sync all fans, and auto-start with Windows. Requires admin (manifest
`requireAdministrator`) so LibreHardwareMonitor can load its kernel driver.

## Key files (all under `pCUE/`)
- `MainWindow.xaml` / `MainWindow.xaml.cs` — the whole UI + all device logic.
- `CorsairLightingProtocolConstants.cs` — Commander PRO HID command bytes.
- `Tachometer/HidTachometer.cs` — external bench tachometer driver (see session log).
- `Properties/AssemblyInfo.cs` — versions.

## How the numeric fan box works (important)
One `UIntegerUpDown` per fan does double duty: on **Set Speed**, value **≤ 100 → PWM power %**
(`WRITE_FAN_POWER` 0x23, a single 0–100 byte), **> 100 → fixed RPM** (`WRITE_FAN_SPEED` 0x24,
16-bit). The Commander PRO's fan-power command is **whole-percent only** — there is no sub-1% PWM
duty over the protocol; finer control comes from fixed RPM (the controller closes the loop in
firmware using the fan's tach wire).

## Hard constraints — do not break
- **LibreHardwareMonitorLib pinned at 0.9.4.** Do not upgrade (0.9.5+ force HidSharp 2.6.4, which
  removes the obsolete `HidSharp.HidDeviceLoader` the Commander PRO code depends on, and switch to a
  RID-split NuGet layout the classic project can't resolve). CPU Temp/MHz/Load are read from LHM.
- **HidSharp pinned at 2.1.0.** The Commander PRO code uses the obsolete `HidDeviceLoader.GetDevices`
  API; the bench tachometer uses the modern `DeviceList.Local` API — both live in 2.1.0.
- **AssemblyVersion stays `1.1.0.0`** (stable identity for settings). **AssemblyFileVersion
  auto-bumps its revision on every _Release_ build** via an inline MSBuild task in the csproj — this
  is expected; don't hand-edit it. Title bar shows the file version at runtime.
- C# language level is 7.3 (no `<LangVersion>` set) — no nullable-reference-type annotations.

## Build
```
MSBuild pCUE\pCUE.csproj /p:Configuration=Debug     # or Release (bumps file version)
```
VS2022 Enterprise MSBuild: `C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe`.
Packages are NuGet-restored (`packages/`, gitignored). App can't be launched by name via the
computer-use access resolver (dev exe isn't Start-menu registered); launch the built exe directly.

## Session log (newest first)
### 2026-08-08 — External bench tachometer support
- Added `pCUE/Tachometer/HidTachometer.cs`: USB-HID bench tachometer driver (**VID 0x1A86 /
  PID 0xE008**, CH340-class HID bridge), **ported from the `Fan Control Application`
  (FanRpmControl) `HidTachometer`**, which was itself adapted from Faganas ATX12V. HidSharp
  transport (reuses the existing 2.1.0 ref — no new package), blocking background read loop,
  auto-reconnect via `DeviceList.Changed`, verbatim 7-segment RPM decode + battery-low flag.
  Adapted to pCUE's C# 7.3 (no nullable annotations) and `Debug.WriteLine` logging.
- Purpose: a fan with **no usable tach wire to the Commander PRO** (e.g. bench-tested 3-pin/2-wire
  fans) still shows real RPM from the external handheld tachometer.
- Wiring in `MainWindow.xaml.cs`: `bench_tach` field + `tachAssignedChannel`; constructed in the
  ctor; disposed in `Window_Closed`. **One injection point** in `FanPollLoop` — for the assigned
  channel, override `rpms[ch]` with `bench_tach.ReadRpm()` **when fresh**, else keep the Commander
  value (user chose "fall back to Commander" on stale/lost). This flows into Current + Min/Max/Avg
  automatically. Note the override only runs while the Commander PRO is connected (the poll loop).
- UI (`MainWindow.xaml`): new **"Bench Tachometer"** GroupBox (Connect/Disconnect, Assign →
  None/Fan #1–6, live RPM readout, status line, BATT LOW flag). Window height grew 450→525 to fit.
  **Status: label + indicator moved to directly under the Sync checkbox** (were far-right).
- Both Debug + Release build clean (only pre-existing warnings). **Runtime RPM values not verified
  here** (no physical tach on the dev machine); the driver logs the first raw HID report + device
  inventory to Debug so the significant-byte offset (index 2 for HidSharp) can be confirmed on the
  real unit — if a reading is 0/wrong, that's the thing to check.
- Measurement only — fan *control* is unchanged (still Commander PRO PWM/RPM).
- File version bumped to 1.3.0.17 (Release). Prior work this session: replaced Core Temp with
  LibreHardwareMonitor (commit 36d8f71).
