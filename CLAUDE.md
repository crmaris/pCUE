# CLAUDE.md

Guidance for AI assistants (and humans) working in this repository.

## What pCUE is

**pCUE** is a lightweight Windows desktop replacement for parts of Corsair's
iCUE software, branded **"pCUE - Cybenetics LTD"**. It is a single-window WPF
app that:

- Talks directly to a **Corsair Commander PRO** fan/RGB hub over USB-HID
  (reads fan RPMs, reads/sets fan connection mode, sets fan speed/power).
- Reads **CPU** temperature, clock and load **in-process** via
  **LibreHardwareMonitorLib** (no external helper process).
- Tracks live **Min / Max / Average** values for CPU and each fan.
- Kills Corsair's iCUE background services on connect, because they fight pCUE
  for exclusive HID access to the Commander PRO.

It is **Windows-only** (WMI, WinForms interop, the Windows registry, native
HID) and cannot be built or run on Linux/macOS. The remote sandbox this agent
runs in can read, edit, and commit the code, but **cannot compile or launch
it** — there is no MSBuild/Visual Studio or attached hardware here.

## Tech stack

- **Language / runtime:** C#, **.NET Framework 4.8** (`TargetFrameworkVersion v4.8`).
- **UI:** WPF (XAML), with some WinForms interop (`System.Windows.Forms.Timer`,
  registry, `Application.ExecutablePath`).
- **Build system:** MSBuild / Visual Studio 2019 (`pCUE.sln`, classic
  non-SDK `.csproj`). NuGet via **`packages.config`** (not PackageReference).
- **Output:** `WinExe` named `pCUE.exe`.

### Key NuGet / DLL dependencies

| Dependency | Purpose |
|---|---|
| `HidSharp` 2.1.0 | USB-HID communication with the Commander PRO |
| `Extended.Wpf.Toolkit` (Xceed) 4.1.0 | WPF controls / AvalonDock |
| `Dirkster.NumericUpDownLib` 2.4.2.2 | `UIntegerUpDown` fan-speed inputs |
| `ModernUI.WPF` 1.0.9 | WPF theming |
| `System.Management` 5.0.0 | WMI queries (`HardwareInfo.cs`) |
| `LibreHardwareMonitorLib` 0.9.4 | In-process CPU temp/clock/load sensors |

> ⚠️ **Build gotcha — restore LibreHardwareMonitorLib:** the CPU-sensor
> dependency is `LibreHardwareMonitorLib` (NuGet, listed in `packages.config`,
> referenced in `pCUE.csproj` with a `HintPath` into `packages\`). A bare
> `nuget restore` of `packages.config` does **not** pull transitive
> dependencies, so the most reliable path is to run
> `Install-Package LibreHardwareMonitorLib` in the Visual Studio Package Manager
> Console — it reconciles `packages.config`, the `<Reference>`, and the
> transitive deps (e.g. `System.IO.Ports`) in one step. If you install a version
> other than 0.9.4, update the version in the `HintPath` to match (or let NuGet
> manage the reference). LibreHardwareMonitor needs the app's **admin rights**
> (already requested in `app.manifest`) for full CPU sensor access.
>
> The old `Core Temp` + `GetCoreTempInfoNET` and `OpenHardwareMonitorLib.dll`
> were removed in the migration; the now-dead files (`Core Temp.exe`,
> `CoreTemp.ini`, `OpenHardwareMonitorLib.dll`, `CoreTempSharedMemory.cs` and
> `Changes.txt`) have been **deleted** from the repo.

## Repository layout

```
pCUE.sln                         Visual Studio solution
pCUE/
  App.xaml / App.xaml.cs         WPF application entry point (minimal)
  MainWindow.xaml                The entire UI (single window, hand-laid-out grid)
  MainWindow.xaml.cs             ~all app logic lives here (see below)
  CorsairLightingProtocolConstants.cs  Corsair Lighting Protocol command opcodes
  HardwareInfo.cs                Static WMI helpers (CPU/board/BIOS/RAM/OS info)
  Properties/
    AssemblyInfo.cs              Version attributes (see Versioning)
    Settings.settings / .Designer.cs   User settings: AutoStart1, AVG_Values
    Resources.resx
  App.config                     Runtime config + persisted user settings
  app.manifest                   Requires elevation / DPI awareness
  small.ico                      App icon
  packages.config                NuGet dependencies
  Readme.txt / License.txt / Tips.txt   Leftover Core Temp docs + scratch notes
```

### Files that are present but NOT compiled

These are in the folder but **not** in the `.csproj` `<Compile>` list, so they
are dead/reference code. Do not assume changes to them affect the build:

- `ScreenCapture.cs` (namespace `Case_Tester`)
- `MessageBoxEx.cs` (namespace `System.Windows.Forms`)

The compiled C# is exactly: `App.xaml.cs`, `MainWindow.xaml.cs`,
`HardwareInfo.cs`, `CorsairLightingProtocolConstants.cs`, plus the generated
`Properties/*` files.

## Architecture & important conventions

Almost all behaviour lives in **`MainWindow.xaml.cs`**, organized with
`#region` blocks: *Main Window Functions*, *Commander Pro Functions*,
*CPU sensors (LibreHardwareMonitor)*, *App Kill functions*. When adding logic,
follow that regioning.

### Commander PRO HID access (read this before touching fan code)

- The device is opened by VID/PID **`0x1b1c` / `0x0c10`** and verified by
  product name `"Commander PRO"`.
- **All HID stream access MUST be serialized through the `hidLock` object.**
  The background poll loop and UI-thread commands (connect, set speed, set
  mode) share one `HidSharp.HidStream` and will corrupt each other if they
  overlap. Every read/write helper takes `lock (hidLock)`; keep that pattern.
- **Fan RPMs are polled on a background `Task`** (`FanPollLoop` /
  `StartFanPolling` / `StopFanPolling`), **never on a UI timer** — a stalled
  HID transfer must not freeze the window. Only final RPM values are marshalled
  back to the UI via `Dispatcher.BeginInvoke`.
- The stream uses 1000 ms read/write timeouts. After
  `MaxConsecutivePollFailures` (3) back-to-back failed passes the app
  **auto-disconnects** via `DisconnectCommanderPro(...)` and updates the status
  label. `DisconnectCommanderPro` is the single, idempotent teardown path —
  reuse it; don't hand-roll disconnect logic.
- The protocol opcodes are in `CorsairLightingProtocolConstants.cs`. HID
  buffers are 64-byte out / 16-byte in; byte 0 is reserved (0), the command
  goes in byte 1, args follow. Fan speed is big-endian in bytes 3–4.
- **Fan speed vs power convention:** in `Set_Fan_Speed_Function_Commander_Pro`,
  a value **≤ 100 is treated as a power percentage**, a value **> 100 as an RPM
  target**. Preserve this when editing fan-set logic.

### CPU data via LibreHardwareMonitor

- A single `Computer { IsCpuEnabled = true }` is opened once in the constructor
  and closed in `Window_Closed`.
- `CpuSensorTimer` (`System.Windows.Forms.Timer`, 500 ms) drives
  `CpuSensorTimer_Tick`. The "Start"/"Stop" button just starts/stops that timer
  — there is **no external process** to launch or kill anymore.
- Each tick refreshes the sensor tree via `thisComputer.Accept(_updateVisitor)`
  (LibreHardwareMonitor only updates `ISensor.Value` on `Update`/`Accept`), then
  picks: a whole-CPU **temperature** (sensor name containing `Package` / `Average`
  / `Tctl`, else the average of the per-`Core` temps), the average per-core
  **clock** (MHz), and the `CPU Total` **load** (%). It writes `CPU_array[0/3/6]`.
- **Number formatting is intentionally left at the current culture** (not
  `cultureUS`) here, so the value round-trips through `Set_min_max`'s
  `Convert.ToDouble` (which also uses the current culture). Don't "fix" this to
  `cultureUS` without also changing the parse side.

### Min / Max / Average

- `Set_Min_Max_AVG_timer` (500 ms) drives `Set_min_max(...)`. UI read-out
  `TextBox`es are named `ed1..ed33` and gathered by walking the logical tree
  (`FindLogicalChildren<T>`) into `CPU_array` / `Fan_array` after a one-shot
  load timer. **Indices into these arrays are positional and load-order
  dependent — do not reorder controls in the XAML without auditing the index
  math** in `Set_min_max` and `UpdateFanRpmUi` (channel→index is `ch*3`).
- The "Average Values" checkbox swaps the CPU Min column for a running average;
  fans always show real Min and have their own dedicated Avg column
  (`ed28..ed33`). This preference persists in `Properties.Settings.AVG_Values`.

### Misc conventions

- Decimal formatting uses an explicit `en-US` culture (`cultureUS`) so the
  decimal separator is always a dot — match this for any numeric parsing/format.
- Persisted user settings are only `AutoStart1` (registry Run-key autostart)
  and `AVG_Values`. Autostart writes
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\pCUE`.
- There are leftover **GPU-Z shared-memory** sensor-index fields and a
  `GpuzShMem.dll` in the solution items that are currently vestigial (no active
  read path). Treat as dead unless you are deliberately wiring GPU-Z back in.
- Some comments are in Greek (`gia`, `me to`, `px.`); this is expected.

## Versioning (important — there is build automation here)

- `AssemblyVersion` is held **stable on purpose** (currently `1.1.0.0`) — it is
  the runtime identity. Do **not** auto-bump it.
- `AssemblyFileVersion` (currently `1.3.0.x`) is the build-stamped version. A
  custom MSBuild `CodeTaskFactory` task (`BumpFileVersionRevision`, target
  `BumpRevisionBeforeCompile` in `pCUE.csproj`) **auto-increments the 4th
  component (revision) on every _Release_ compile**. Debug builds do not bump,
  to avoid churning `AssemblyInfo.cs` during development.
- The window title shows the live file version at runtime
  (`pCUE - Cybenetics LTD - v.<FileVersion>`).
- When asked to "bump the version" for a release, edit the major/minor/build of
  `AssemblyFileVersion` in `Properties/AssemblyInfo.cs`; let the Release build
  handle the revision. Do not commit a manually-bumped revision and a Release
  build of the same change (you'd double-count).

## Build & run (Windows only)

```
# Restore + build from a Developer Command Prompt / VS:
nuget restore pCUE.sln          # or let VS restore packages.config
msbuild pCUE.sln /p:Configuration=Release   # bumps file-version revision
# Debug build does NOT bump the version:
msbuild pCUE.sln /p:Configuration=Debug
```

Running requires: a real **Corsair Commander PRO** attached for fan features and
**administrator rights** (the app manifest requests elevation; needed both to
kill iCUE services and for LibreHardwareMonitor's CPU sensors). No external
helper executable is needed anymore.

There are **no automated tests, linters, or CI** in this repository. "Verifying"
a change here means a Windows build + manual hardware test; state plainly that
you could not build/run when working in the Linux sandbox.

## Git workflow for agents

- Active development branch for this task: **`claude/claude-md-docs-1evvph`**.
  Develop, commit, and push there; never push to `master` without explicit
  permission.
- Push with `git push -u origin <branch>`; retry network failures with
  exponential backoff.
- Do **not** open a pull request unless explicitly asked.
- Build outputs (`bin/`, `obj/`), `*.user`, `*.suo`, and `packages/` are
  git-ignored (standard VisualStudio `.gitignore`). Don't commit them.

## Quick orientation for common tasks

- **Fan reading/control bug** → `MainWindow.xaml.cs`, *Commander Pro Functions*
  region; respect `hidLock` and the background poll model.
- **New Corsair command** → add the opcode to
  `CorsairLightingProtocolConstants.cs`, then a locked read/write helper.
- **CPU/sensor info** → `HardwareInfo.cs` (WMI) or the *CPU sensors
  (LibreHardwareMonitor)* region (`CpuSensorTimer_Tick` + `UpdateVisitor`).
- **UI layout/labels** → `MainWindow.xaml`; remember the `ed1..ed33` index
  coupling described above.
- **Release/versioning** → `Properties/AssemblyInfo.cs` + the MSBuild bump task
  in `pCUE.csproj`.
