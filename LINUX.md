# pCUE on Linux

pCUE Linux is a native Avalonia desktop app targeting **Ubuntu 22.04/24.04 and
Debian 12/13, x86-64**, using X11 or XWayland. It bundles .NET 10: no SDK, Mono,
Wine or separately installed .NET runtime is needed. The Windows WPF app remains
available alongside this Linux build.

## Install

Download `pcue-linux_<version>_amd64.deb` and its SHA-256 sidecar from the supplied
package location. In the directory containing both files:

```sh
sha256sum -c pcue-linux_<version>_amd64.deb.sha256
sudo apt install ./pcue-linux_<version>_amd64.deb
pcue-linux
```

The package installs a menu entry, `/usr/bin/pcue-linux`, bundled runtime files in
`/opt/pcue-linux`, and narrowly scoped USB-access rules. Reconnect the USB device
after installing. Run the GUI as your normal desktop user, not as root. The rules
grant access only to an active local session; headless/SSH users need an operator
configured per-device ACL. Never grant blanket access to all HID devices.

**Updates are automatic.** The Debian installer enables
`pcue-linux-update.timer`, which checks the public release feed every 30 minutes.
It verifies SHA-256, package identity, version and architecture, then installs
newer packages without prompts. A cross-process lock defers installation while
an installed pCUE Linux GUI is running; the next launch uses the new package.
The timer can be disabled with `sudo systemctl disable --now pcue-linux-update.timer`.

The portable app checks the same feed automatically every 30 minutes. When
controllers are disconnected and recovery is complete, it prepares and verifies
the whole new runtime before closing, swaps the app directory and restarts.
Python 3 and a user-owned writable application directory are required. Failed
downloads leave the current app open. Tokens, telemetry and saved recovery stay
outside the replaced application directory.

For the portable archive, verify its sidecar, extract it with `tar -xzf`, and run
`./pcue-linux`. Desktop libraries are required: libc 2.35+, libstdc++, OpenSSL 3,
ICU, libfontconfig, libX11, libICE, libSM, libudev and zlib. To enable USB access:

```sh
sudo install -m 0644 70-pcue-linux.rules /etc/udev/rules.d/70-pcue-linux.rules
sudo udevadm control --reload-rules
# Reconnect the controller, then start ./pcue-linux as the desktop user.
```

## Features and limits

| Feature | Linux build |
|---|---|
| Commander PRO | Six outputs, manual whole-percent power, explicit 3/4-pin modes, four-pin hardware RPM targets |
| Bench HID tachometer | Shared decoder, assignment and software RPM hold |
| CORE / CORE XT | Shared percentage driver; exact USB-interface selection and saved recovery |
| iCUE LINK hub | Shared percentage driver, chain identity and saved recovery |
| Aquacomputer OCTO | Shared percentage driver, selected settings capture/restore |
| CPU telemetry | Linux `/proc` load/frequency; available coretemp/k10temp/zenpower temperatures |
| Remote pCUE | Existing authenticated protocol-v2/SSE client, reconnect, selected fan commands and RPM hold |
| Local API | Optional `--serve`, loopback-only port 5056; ordinary Commander PRO routes |
| be quiet IO, motherboard PWM | Unavailable; their current adapters depend on Windows native APIs / LHM |
| Acoustic acquisition leases | Unavailable locally; the experimental Linux package is not a commissioned lab fixture |
| Windows services, updater, scheduled tasks | Use Linux desktop/package-management tools instead |

USB streams stay closed at startup. Select a device and Connect; select outputs
and Apply to change them. Stop other tools such as liquidctl, CoolerControl and
vendor software before taking control. Pumps require at least 25%; that software
floor does not establish a safe operating range for every physical pump.

CORE/XT, LINK and OCTO save the selected settings before the first write. Stop /
restore reopens only the exact saved device, restores and verifies the baseline,
and removes recovery only after success. A failed restore prevents normal exit and
retains `cooling-recovery.json` under `$XDG_STATE_HOME/pcue-linux` (default
`~/.local/state/pcue-linux`). Reconnect to the same USB port if recovery is pending;
never delete this file to bypass recovery. Commander PRO follows the Windows
manual-control behavior: its last commanded settings remain after disconnect.

The source uses the same protocol code as Windows, but **Linux hardware behavior
still needs physical commissioning**. Linux launch, rendering, package extraction
and simulated tests do not prove USB control on a physical Linux computer. WSL
normally has no attached USB controller; an empty inventory there is expected.

```sh
pcue-linux --help
pcue-linux --version
pcue-linux --inventory                   # read-only inventory, no control session
PCUE_TOKEN='<token>' pcue-linux --remote 192.168.1.20 --port 5056
pcue-linux --serve                       # optional localhost API, off by default
```

The remote token stays in memory. The GUI exposes selected-output commands only;
unselected remote fans are never included in Apply. A remote connection does not
open the Linux computer's USB controller. Local API acquisition calls return an
unsupported error; no acoustic-control capability is claimed.

## Build and validate

The Linux source is `linux/pCUE.Linux.csproj`. Install .NET SDK 10 to build from
source. Locked dependencies are in `linux/packages.lock.json`.

```sh
dotnet build linux/pCUE.Linux.csproj --locked-mode
dotnet run --project linux/tests/pCUE.Linux.Tests.csproj
dotnet publish linux/pCUE.Linux.csproj -c Release -r linux-x64 --self-contained true --locked-mode -o .codex-tmp/linux/publish
python3 build/package-linux.py --publish .codex-tmp/linux/publish --output artifacts --version <stamped-version> --epoch "$(git log -1 --format=%ct)"
```

Agents on the owner's Windows workstation must use `safe-dotnet.ps1` for .NET
gates. `build/pack-linux.ps1` applies it automatically. Linux packaging reads the
Windows release version without incrementing it; the approved Windows release
pack remains the sole version-bump authority. Both packages refuse overwrite.
