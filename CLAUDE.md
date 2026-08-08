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
16-bit). This overloading is **deliberate and unambiguous** — no real fan runs below 100 RPM, so
that band is free to mean percent (owner, 2026-08-08). Don't "fix" it with a mode selector.

The Commander PRO's fan-power command is **whole-percent only** — there is no sub-1% duty over the
protocol.

### Commander PRO hardware limits (verified on the bench, 2026-08-08)
- **Fixed-RPM targeting (0x24) works on 4-pin/PWM channels only.** On a **3-pin (DC)** channel the
  Commander offers fixed **percent** only — commanding an RPM there does nothing and the fan can sit
  still. This is a firmware limitation, not a pCUE bug, and it is why the closed-loop RPM hold below
  exists. (Owner confirmed; matches iCUE's own behaviour.)
- A 3-pin fan **does** have a speed-sense wire (pin 3) — the *4th* wire is PWM control, not sense.
  Wire **colours vary by maker** (this lab has fans using purple, not the conventional yellow, as
  the third wire), so never infer function from colour: the definitive test is whether the Current
  column reports RPM for that channel.
- 3-pin fans are driven by **voltage**, so they will not start below roughly 7 V — a target under the
  fan's start-up speed leaves it stationary.
- Packet layouts were verified against the reverse-engineered protocol and are correct:
  `0x28` = `[0x02, fan, mode]`, `0x23` = `[fan, duty]`, `0x24` = `[fan, rpm_hi, rpm_lo]`.
- Known gap: pCUE **never checks the device's response status byte** (`0x00` OK / `0x01` error) on
  any write, so a rejected command fails silently. Worth adding.

## Closed-loop RPM hold (`pCUE/Control/FanRpmHoldController.cs`)
Because the Commander will not regulate by RPM on a DC channel, pCUE closes that loop in software:
`target RPM → controller → Commander duty % (0x23) → fan → RPM feedback`. The strategy is ported
from the Fan Control Application's `FanRpmController` (step-based proportional: coarse step when far
from target, fine step when near — deliberately not an aggressive PID).

**Feedback source is whatever is live.** `ReadHeldFanRpm()` returns the value pCUE already shows in
the Current column, which is the bench tachometer when one is assigned to that fan and fresh, and
the Commander's own tach reading otherwise. So one loop covers both a fan with no usable tach wire
and a 3-pin fan the Commander can read but won't regulate. It refuses to start with no feedback
rather than running blind.

**One deliberate difference from the original:** its actuator was a PSU voltage with millivolt
resolution; ours is whole-percent duty, so the finest move is 1% (~20–50 RPM on a real fan). A
tolerance tighter than one duty step is unreachable and a naive loop would oscillate forever, so
`ResolutionLimitReversals` detects the bracketing, parks on the closer duty and reports
"at 1% duty resolution limit". If finer is ever needed, the next step is **duty dithering**
(alternate 43/44% so inertia averages to ~43.5%) — not yet implemented.

Stops on: user Stop, lost feedback (8 consecutive bad samples — **fan is left at its current duty**,
still cooling), saturation at 0/100% while off target, timeout to first stable, manual **Set Speed**
(the user taking over), Commander disconnect, and app close.

## Hard constraints — do not break
- **LibreHardwareMonitorLib pinned at 0.9.4.** Do not upgrade (0.9.5+ force HidSharp 2.6.4, which
  removes the obsolete `HidSharp.HidDeviceLoader` the Commander PRO code depends on, and switch to a
  RID-split NuGet layout the classic project can't resolve). CPU Temp/MHz/Load are read from LHM.
- **HidSharp pinned at 2.1.0.** The Commander PRO code uses the obsolete `HidDeviceLoader.GetDevices`
  API; the bench tachometer uses the modern `DeviceList.Local` API — both live in 2.1.0.
- **AssemblyVersion stays `1.1.0.0`** (stable identity for settings). **AssemblyFileVersion
  auto-bumps its revision on every _Release_ build** via an inline MSBuild task in the csproj — this
  is expected; don't hand-edit it. It is the version the updater compares, and the title bar shows
  it at runtime. Note every Release build (including each `pack-release.ps1` run) bumps it.
- **C# language version is pinned to `12.0`** (`<LangVersion>` in the csproj) on .NET Framework 4.8.
  Language-only features work. Anything needing new **runtime** types does not: records/`init` need
  an `IsExternalInit` shim, async streams need `Microsoft.Bcl.AsyncInterfaces`, `Index`/`Range` need
  their own types, and default interface members are impossible. Don't enable `<Nullable>` — the
  existing code isn't annotated and it would bury the build in warnings.

## Build
```
MSBuild pCUE\pCUE.csproj /p:Configuration=Debug     # or Release (bumps file version)
```
VS2022 Enterprise MSBuild: `C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe`.
Packages are NuGet-restored (`packages/`, gitignored). App can't be launched by name via the
computer-use access resolver (dev exe isn't Start-menu registered); launch the built exe directly.

## Release packaging  (`build/`)
**STANDING RULE (owner, 2026-08-08): ALWAYS build the installer.** Every time you finish a change
worth committing, run `pack-release.ps1` so `artifacts/pCUE_<ver>_setup.exe` exists and matches the
committed source. Do not leave a session with code changes but no installer built.

```powershell
pwsh build\pack-release.ps1                      # portable zip + single-file installer
pwsh build\pack-release.ps1 -SkipInstaller       # zip only
pwsh build\pack-release.ps1 -Thumbprint <cert>   # signed (see build\sign.ps1)
```
Outputs land in `artifacts/` (gitignored): `pCUE_<ver>_setup.exe` (Inno Setup, ~2.8 MB),
`pCUE_<ver>_portable.zip`, each with a `.sha256`.

**Never package `pCUE\bin\Release` directly** — it accumulates files from older builds (Core Temp,
`OpenHardwareMonitorLib`, `*.vshost.*`) that must not ship. The script deliberately *rebuilds into
an empty* `artifacts\stage\pCUE` so only what the project currently needs is staged, then drops
`*.pdb`/`*.xml`.

Two ordering rules the script encodes — **do not "simplify" them**: the staged app exe is signed
**before** packaging (signing `setup.exe` does not sign its payload), and each artifact is signed
**before** its `.sha256` is computed.

Installer facts: `build/installer/pCUE.iss`, `AppId` GUID `{FFD36531-…}` — **never change it**, it
is how Windows recognises an in-place upgrade. `PrivilegesRequired=admin` (the app needs it),
`CloseApplications=yes` so an update can replace a running `pCUE.exe`, and the HKCU `Run` value is
removed on **uninstall only** so an in-place update keeps the user's Auto Start choice.

## Remote control + debug logging
Modelled on Powenetics V3's `RemoteControlServer`, so the two apps behave the same way. **Off unless
asked for on the command line** — nothing is persisted and no token is ever written to disk.

```
pCUE.exe --remote --debug                                                  # loopback only
pCUE.exe --remote-prefix=http://+:5056/ --remote-token=SECRET --debug      # LAN
```

- `--debug` sets the log to **Debug** and mirrors it to
  `%LOCALAPPDATA%\pCUE\logs\pcue_<stamp>.log`.
- **Security:** loopback is always allowed; **any non-loopback request is refused unless a token
  matches**, so widening the prefix cannot accidentally expose unauthenticated fan control. Token
  goes in `X-pCUE-Token` or `?token=`. Binding a non-loopback prefix needs elevation (pCUE already
  runs elevated) and a firewall rule for TCP 5056 / UDP 5057.
- **Discovery** (`Remote/DiscoveryBeacon.cs`): a *passive* UDP responder on 5057 — it answers a
  `PCUE_DISCOVER` probe with app/version/host/url/requiresToken and is otherwise silent. It never
  returns the token.
- **CLI:** `tools/pcue-cli.ps1` (discover, status, log, debug, duty, rpm, mode, hold, stop, open,
  close, cpu-start/stop, tach-connect/disconnect, assign, reset, watch). `GET /` lists every
  endpoint at runtime.

### Logging (`pCUE/Diagnostics/AppLog.cs`) — read this before adding diagnostics
The rest of the app logs through `Debug.WriteLine`, which is `[Conditional("DEBUG")]` and therefore
**compiled out of Release builds** — the builds users actually run produced *no* diagnostics, which
made a remote hardware fault undebuggable. **Use `AppLog` for anything that must survive Release.**
It keeps a 4000-line ring buffer (served by `GET /log`) plus an optional file.

Every Commander PRO write is now traced with its command bytes **and the device's reply**, including
the status byte (`0x00` OK / `0x01` error) that pCUE previously discarded — a rejected command used
to look identical to a successful one.

## In-app updater  (`pCUE/Updates/AppUpdateService.cs`)
Mirrors the Powenetics V2/V3 component updater. Reads the shared **public** manifest
`https://raw.githubusercontent.com/crmaris/powenetics-updates/main/components.json`
(overridable via the `Update_Manifest_Url` setting) and compares `apps.pcue.version` with the
running exe's file version. UI is the bottom strip: **Check for Updates**, an **On start** checkbox
(`Update_Check_On_Start`, default on), and a status line.

Security rules enforced in the service — **keep them**: HTTPS only; the manifest must supply a
`sha256` and a mismatched download is deleted and rejected; a download is **never executed
automatically** — the user confirms twice (download, then install) before the installer is launched
and the app shuts down (it cannot overwrite its own running files). The start-up check only
*reports*; it never pops a dialog and never installs.

**To publish a release:** run `pack-release.ps1`, attach `pCUE_<ver>_setup.exe` to a GitHub Release
on `crmaris/pCUE` (the repo is public, so its release assets are anonymously downloadable), then set
`apps.pcue` `{version, url, sha256}` in the `crmaris/powenetics-updates` manifest. The pack script
prints the exact values. Do **not** point the manifest at `updates.cybenetics.com` — that is Faganas
Light's commercial licensing endpoint, not an app-update feed.

## Bench validation of the RPM hold (2026-08-08, DESKTOP-OU4447V, remote API)
Driven entirely over the remote API against a real Commander PRO + handheld tachometer.

**The fan under test is a PWM fan whose tach never reaches the Commander** (every channel reads
0 rpm). Channel 2, measured:

| Fan 2 mode | 100% | 50% | 20% |
|---|---|---|---|
| **4-pin (PWM)** | 2292 rpm | 1368 rpm | 608 rpm |
| **3-pin (DC)** | will not run | 0 | — |

So for this fan the mode must be **4-pin**. Setting it to 3-pin (the intuitive choice for "no PWM
wire") stops it dead — a PWM fan's controller generally will not run on reduced voltage. Leaving the
channel on **auto** is also wrong: the Commander mis-detects it and ignores duty entirely.

**Closed-loop hold results** (feedback from the bench tachometer):

| Target | Tolerance | Settles at | Duty |
|---|---|---|---|
| 1500 | ±50 (old default) | 1464 rpm | 55% |
| 1500 | ±15 | 1510 rpm | 57% |
| 1200 | ±20 (shipped) | **1200 rpm, err 0** | 43% |

1% duty ≈ 20–25 rpm on this fan, which is why ±50 stopped a full duty step early. Convergence is
monotonic and takes ~40 s with the 4 s settle delay.

## Session log (newest first)
### 2026-08-08 — Installer + in-app updater, C# 12, tach review fixes
- **Release packaging added** (`build/`): `pack-release.ps1`, `installer/pCUE.iss`, `sign.ps1`.
  Produces a single `pCUE_<ver>_setup.exe` (~2.8 MB) + portable zip + `.sha256`. See the *Release
  packaging* section above for the rules (clean stage, sign-before-package, sign-before-hash).
- **In-app updater added** (`pCUE/Updates/AppUpdateService.cs` + bottom UI strip). Verified live
  against the real manifest: it fetches over HTTPS and reports "no entry for pCUE yet" until
  `apps.pcue` is published. See the *In-app updater* section for the security rules and the
  publish steps.
- **C# raised 7.3 → 12.0** (`<LangVersion>` in the csproj). Confirmed `/langversion:12.0` reaches
  csc and that C# 8/9 constructs compile on net48. Added framework refs `System.Net.Http` and
  `System.Web.Extensions` (the updater parses JSON with `JavaScriptSerializer`, so **no new NuGet
  package** and nothing extra to ship).
- **Fixed 3 defects confirmed by an adversarial review of the tachometer commit (fb8a7ff):**
  1. `Set_min_max`: the Min-column block sat **outside** the `> 0` guard, so a single 0 sample
     overwrote an established minimum and was then re-seeded from the next reading — permanently
     losing the real Min. Now gated on `current > 0`. (Latent before; the tach override's mixed
     real/zero stream is what made it bite: for a fan with no Commander tach wire the "fall back
     to Commander" value *is* a hard 0.)
  2. Tach panel showed a **stale RPM forever** when the tach was still enumerated but had stopped
     sending frames (auto power-off / blocked beam) — `ReadingChanged` only fires on a successful
     decode. The panel is now refreshed by `Update_Tach_Panel()` on the existing 500 ms UI timer
     via `ReadRpm()`, so panel and fan column share one definition of "fresh"; it shows
     "no signal" instead of lying. The `ReadingChanged` subscription was dropped.
  3. **Pre-existing:** `Fan6_Numeric` had no `ValueChanged` handler (fans 1–5 did), so typing in
     Fan #6's box never moved its slider. Handler added.
- **UI:** bottom-strip text was orange/lime on the bright green end of the gradient and unreadable;
  now white for normal state, yellow for attention (`UpdateInfoBrush`/`UpdateAlertBrush`).
  `Status_Label` (Commander) still uses the old lime/orange palette — it sits higher up on a darker
  background and was not reported as a problem.
- **Known, not yet actioned:** `Extended.Wpf.Toolkit` is referenced but **completely unused**
  (0 `xctk:` in XAML, 0 in code) and accounts for ~2 MB of the 2.9 MB staged build. Removing it
  would cut the installer to roughly 1 MB. Owner decision pending.

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
