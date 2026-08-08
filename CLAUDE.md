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
  auto-bumps on every _Release_ build** via an inline MSBuild task in the csproj — expected; don't
  hand-edit it. It is the version the updater compares, and the title bar shows it at runtime. Every
  Release build (including each `pack-release.ps1` run) bumps it.
  **The scheme is "+0.1" on a THREE-part version, and the last component is a tenth that carries:**
  `1.3.8 → 1.3.9 → 1.4.0 → 1.4.1`. It never reaches `1.3.10` (owner decision, 2026-08-08).
  Comparison is `System.Version`, so a two-digit component would still have sorted correctly — the
  rollover is a presentation decision, not a correctness one.
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

1% duty ≈ 20–25 rpm on this fan, which is why ±50 stopped a full duty step early.

### Both approach directions verified on 1.4.3 (2026-08-08)

Every test above happened to start from a speed **below** the target, which is exactly why the two
1.4.1 defects hid. Re-validated on 1.4.3 in both directions, ±20 tolerance:

| Start | Target | Result | Duty | Time to Stable |
|---|---|---|---|---|
| 1410 rpm (52%) | 1100 — **descending** | 1097–1110, held 75 s | 39% | ~28 s |
| 1105 rpm (40%) | 1600 — **ascending** | 1593–1597, held 33 s | 61% | ~45 s |

**Convergence is monotonic only when ascending.** Descending it undershoots — the first coarse
−5% step took 1410 → 1026 (74 rpm *past* target) and it then climbed back 36→37→38→39%. That is
expected and harmless, but it is the exact shape that made 1.4.1 declare a false `Stable`, so don't
"tidy" the reversal handling without re-running the descending case.

## UI conventions worth keeping
- **The fan numeric box is deliberately overloaded**: `≤100` = power %, `>100` = RPM. No real fan
  runs below 100 RPM, so the split is unambiguous. Don't "fix" it with a mode selector.
- **The RPM hold is reached through the normal Set Speed button**, not its own controls: pick the
  fan the tachometer is on, tick *Adjust fan speed from Tacho*, type an RPM, press Set Speed
  (owner's simplification, 2026-08-08). An earlier design had a second target box and a Hold button
  — it duplicated existing controls and let the two fan selectors disagree, which silently produced
  a loop with no feedback.
- **The bottom strip sits on the bright end of the gradient** — use white for normal state and
  yellow for attention. Orange and lime are unreadable down there.
- The app icon is generated by `tools/make-icon.ps1` (procedural, 9 sizes) rather than kept only as
  an opaque binary. Re-run it to tweak the design.
- Help lives in `HelpWindow.xaml`, opened by the `?` button. Keep it in plain language, and keep the
  two traps that actually cost bench time: the fan-mode drop-down must match the fan, and a fixed
  RPM target only works if the Commander can read that fan's sense wire.

## Session log (newest first)
### 2026-08-08 — Tachometer decode: phantom zero and culture-sensitive parse (inherited bugs)

Two defects found while working on the **Fan Control Application**, the app this driver was
**ported from** (`pCUE/Tachometer/HidTachometer.cs` ← `FanRpmControl` ← Faganas ATX12V). Both were
in the original and came across with the port; both are now fixed here.

1. **A frame that failed to decode published a fake `0 rpm`.** `realRpm` started at 0, a failed
   parse left it 0, and it was published anyway — so a garbled frame, a blank display or an "L"
   under-range marker was indistinguishable from a genuinely stopped fan. **In the Faganas apps
   that behaviour is correct and was deliberately left alone** (measurement app: "nothing
   measured" IS zero, and their DataCheck reads 0 as "designed zero-rpm mode or no tacho signal").
   **pCUE is not a measurement app.** This feeds the closed-loop RPM hold, where a phantom zero is
   a large fake error that drives the duty UP — and because the frame still refreshed
   `_latestRpmUtc` it *looked fresh*, so `StalenessMs` and the lost-signal path never fired.
   Now nothing is published on a failed decode and `ReadRpm()` goes stale, which the hold loop
   already knows how to handle.

2. **The parse used the current culture.** The segment table emits `.` as the display's decimal
   point; on a culture where `.` is the GROUP separator (de-DE, fr-FR, el-GR) `123.4` read as
   `1234`. Both parses are now `NumberStyles.Float` + `CultureInfo.InvariantCulture`. The bench is
   en-US (verified), so this was latent here.

A real all-zeros display still decodes and publishes as a genuine 0. Builds clean.

**Why this is lower-risk than "a control change that has not been on the bench" usually is.** The
null path it now feeds is not new — `FanRpmHoldController` already counts unreadable samples to
`MaxInvalidRpmSamples` and then deliberately splits two cases: sensor died → hold the duty (the fan
keeps cooling), versus we stalled the fan → back off to `lastGoodDuty`. That code's own comment
names the exact ambiguity this fix removes: *"a stopped fan reads 0, which looks exactly like a
dead tachometer."* Before, an unreadable frame WAS a 0 and could never reach that logic; now a
genuinely stopped fan still reads a real 0 (the display sends "000000", which decodes), and only
unreadable frames go down the lost-signal path the controller was written for. So the change routes
a case into existing designed handling rather than introducing new behaviour.

**Still worth one deliberate bench check**, because that path has not been exercised on hardware
since: start a hold, block the tachometer's optical beam, and confirm it reports lost signal and
holds duty instead of winding the duty up.

Reference implementation, extracted and unit-tested: `TachoFrameDecoder` in FanRpmControl.

### 2026-08-08 — v1.5.0: duty read-back closes the last "kick to 40%" case
The Commander PRO keeps its fan duty across a pCUE restart; `lastCommandedDuty[]` is per-session,
so the **first** hold of every session used to ignore a perfectly good running fan and kick it to
40%. `READ_FAN_POWER` (0x22) is now the fallback when pCUE has not commanded a duty itself.

- **The `inbuf[2]` offset was never actually a guess**, though it had never been called:
  `Commander_Pro_READ_FAN_Speed` — used by the poll loop and known to report correct RPM — parses
  `inbuf[2] << 8 | inbuf[3]`, so payload data starts at `inbuf[2]` and the power read uses the same
  convention. Verified anyway against ground truth (below).
- **Both sources are logged on every hold start, used or not**, so the device read-back can always
  be checked against what pCUE believes it commanded. Keep this — it is what made the verification
  below a measurement rather than an assumption.
- *Verified end to end:* fan left at **33% duty / 932 rpm**, pCUE updated (restart wipes the
  tracker), Commander re-opened without commanding any duty. First hold logged
  `pCUE tracked=none, Commander reports=33%` and started at 33%. Converged to 1000 rpm in **12.1 s**,
  monotonic 33→34→35, peak 990, no overshoot. A failed read returns 0 and degrades to the old kick.
- Also re-checked after the 1.4.8 revert: 350→400 on **1.4.9** settles in **16.6 s**, monotonic
  13→14→15, peak 411, no hunting. The revert is clean.

### 2026-08-08 — v1.4.8 tried to make the hold faster, oscillated, and was reverted (v1.4.9)
**Read this before trying to speed up the hold loop again — the obvious optimisation is a trap.**

Owner asked for faster convergence "without losing stabilization". 1.4.8 tried two things:
size each step from the fan's measured RPM-per-percent instead of a fixed coarse/fine pair, and
scale the settle wait to the step size (2500 ms for a 1% move instead of a flat 4000 ms).

**It made things much worse.** Same 350→400 RPM test, same fan, measured on the bench:

| Build | Time to Stable | Peak RPM | Behaviour |
|---|---|---|---|
| 1.4.3 | 33.2 s | 1063 | detour via the fixed 40% start |
| **1.4.6 / 1.4.9** | **12.1 s** | **391** | clean, monotonic |
| 1.4.8 | 59.5 s | 507 | **hunted**: duty 16→15→13→14→16→18→16→13→12→13→14→16→18 |

Two wrong assumptions, both pushing the same way:
- **"A small duty step settles almost instantly."** It does not, at low RPM. A fan turning at
  ~300–400 rpm has very little torque and takes **8–10 s** to reach a new steady speed. The log is
  unambiguous: after stepping to 16% duty the RPM read **383** at settle-end and kept climbing to
  423, then 437, over the following five seconds. The loop corrected against a value the fan had
  already left, then corrected the correction.
- **`rpm/duty` is not the incremental slope.** Seeding the step estimate from the operating point
  gave 24.5 rpm/%, but the slope actually measured across that move was **35.8 rpm/%**, so the step
  was oversized as well as under-settled.

**The tachometer's refresh rate was measured properly and is worth keeping:** polling a steady fan
at 250 ms, the meter emits a new value every **1873 ms median (1698 min / 2145 max)**. That is a
hard floor on any settle time — below it you re-read the previous value rather than a fresh one.
The measurement was right; the conclusion drawn from it was not, because the *fan* is slower than
the meter at low RPM.

**Conclusion: ~12 s for a two-step change is close to the floor**, which is roughly
(one meter refresh + the fan's mechanical settling) per step, plus the 3 s stabilization window.
Meaningful gains need a faster feedback source, not a cleverer loop. Do not re-attempt this
without first measuring the fan's settling time *at the RPM in question*.

**Trap discovered while reverting:** `git revert` of a commit that included the packed version bump
rolls `AssemblyFileVersion` **backwards** (here to 1.4.7, below the already-released 1.4.8). The
in-app updater compares versions, so the fix would never have been offered to anyone on 1.4.8.
After reverting a released commit, always check `AssemblyInfo.cs` and hand-set it above the highest
published version before packing.

### 2026-08-08 — v1.4.4/1.4.5/1.4.6: hold deadlock, battery warning, start-from-current-duty
All three shipped and **verified on the bench on 1.4.6**.

- **1.4.4 — the hold could strand a fan, and it happened to the owner.** `StartRpmHold` refused
  whenever `ReadHeldFanRpm()` was null, and a **stopped fan reads 0**, which that helper reports as
  "no reading". It returned **without writing any duty**, so the fan could not start, so the reading
  stayed 0, so every later Set Speed was refused too — self-latching, and the message blamed the
  tachometer, which was connected and fine. Typing a *higher* RPM did not help, which is the
  symptom to recognise. Now gated on `HasFreshRpmSource()` ("can this channel be measured at all")
  rather than "is it turning this instant".
  Second half, in the controller: driving the duty below the fan's start-up point stalls it, and the
  resulting zeros were reported as a lost tachometer signal with the duty left where it was, i.e.
  stopped. It now tracks `lastGoodDuty` and, on a run of bad samples, **restores it** and reports
  "target is below this fan's minimum speed".
  *Verified:* fan stopped dead, `/hold/start rpm=800` → accepted, kicked to 40%, Stable at 786 rpm
  in 30 s. On 1.4.3 the same call was refused outright and the fan stayed dead.
- **1.4.5 — low-battery warning made visible.** The `BATT LOW` label existed but was default-size
  text at the far right, and it followed the meter's **instantaneous** flag. That flag flickers (LOW
  at 14:18, clear minutes later on the same cell), so the label blinked on and off and the first
  real low battery in this app's life was only ever seen in the log. Now 16 pt bold yellow, and it
  **latches** until the meter disconnects — which is what changing the cell does. The modal warning
  is no longer re-armed when the flag merely goes clear, or one tired cell would pop it repeatedly.
  **Not verified on hardware** — the owner fitted a fresh cell before it could be tested.
- **1.4.6 — the hold no longer takes a detour through 40% duty.** `StartDuty` was a fixed 40%, so
  *every* hold began by slamming the fan there regardless of where it already was. The smaller the
  requested change, the more absurd the trip. Measured on 1.4.3, holding 400 RPM from 318 RPM/12%:
  duty forced to 40%, fan shot to **1063 rpm** (3× the target, wrong direction), then walked back
  down over **33.2 s** to settle at **14%** — half a minute to move the duty two points.
  Now opens at the duty pCUE last commanded on that channel (`lastCommandedDuty[]`), and skips the
  initial settle wait when starting from the current duty (nothing to settle, nothing stale).
  *Verified on 1.4.6:* same test → **Stable in 12.1 s, peak 391 rpm, no excursion.**
- **Known gap, not fixed:** `lastCommandedDuty` is per-session, so **the first hold after an app
  restart still falls back to the 40% kick** even on a spinning fan. `Commander_Pro_READ_FAN_Power`
  (0x22) exists and would close this, but **it has never been called** — it is dead code and the
  `inbuf[2]` offset is unverified. Validate it against a known duty before trusting it.
- **Correction to an earlier claim in this file:** 300 RPM was said to be below this fan's floor,
  extrapolated from `20% → 608 rpm`. The fan actually runs at ~318 rpm at 12%, so 300 is probably
  reachable and that inference was wrong. The deadlock was real regardless.

### 2026-08-08 — v1.4.3 RPM-hold fix validated on the bench (both directions)
- **The two 1.4.3 fixes in `FanRpmHoldController` are now confirmed on real hardware**, not just by
  reasoning. Bench updated 1.4.1 → 1.4.3 via the in-app auto-updater (which worked end to end: the
  remote API went dark for ~30 s while the installer replaced the running exe, then came back on
  1.4.3). See the *Both approach directions verified* table above for the numbers.
- **Fix 1 — stale initial reading.** Log shows `HOLD duty -> 40%` at 14:07:53.067 and the first
  sample at 14:07:57.191: a 4 s gap, and that first reading is 1323 (already falling), not the
  pre-hold 1410. Before the fix the loop read the *old* speed and drove the first correction the
  wrong way.
- **Fix 2 — false `Stable`.** During the descending run the duty reversed (40→35→36) while the error
  was +73/+74. That reversal pattern is what made 1.4.1 park and report `Stable` 189 rpm off target.
  On 1.4.3 the `nearEnough` gate (`|err| <= tolerance*3`) held it in `Ramping` until err = −6.
- **A trap for the next session:** after an update the app restarts with **nothing connected** —
  Commander closed, tachometer closed, no fan assigned, `hold.status = Idle`. Re-arm with
  `/commander/open`, `/tach/connect`, `/tach/assign?fan=N` before any hold test.
- **`hold.duty` in `/status` is the controller's last value, not the live duty.** With the hold
  stopped it reports whatever the previous run ended on, which does not track a manual
  `/fan/duty`. Cost ~5 minutes here (read 32% while the fan was really at ~50%/1366 rpm). Trust
  the rpm reading, or the `WRITE_FAN_POWER` line in the log, not this field.
- No code changed this session — validation only.

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
