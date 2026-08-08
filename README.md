# pCUE

A small, fast fan controller for the **Corsair Commander PRO**. Far less than iCUE does, but easier
to drive and a fraction of the size.

- Per-fan speed: power **%** or a fixed **RPM**
- Fan mode per channel: Auto / 3-pin (DC) / 4-pin (PWM) / Disconnect
- Live CPU temperature, clock and load (via LibreHardwareMonitor)
- Min / Max / Average per fan and for the CPU
- Closed-loop **RPM hold** using an external bench tachometer
- In-app updates, and an optional HTTP remote-control API

Requires Windows and .NET Framework 4.8 (shipped with Windows 10 1903+ and Windows 11). pCUE runs
elevated, because reading CPU sensors needs it.

## Install

Download `pCUE_<version>_setup.exe` from [Releases](https://github.com/crmaris/pCUE/releases), or
take the portable `.zip` and unzip it. The installer is not code-signed, so SmartScreen will warn on
first run; SHA-256 checksums are published with every release.

## The bench tachometer

Some fans cannot be speed-regulated by the Commander PRO at all, because their **speed-sense wire
never reaches it** — the Commander reads 0 RPM for the channel, so its own fixed-RPM mode has no
feedback to close the loop on. (Separately, the Commander only regulates by RPM on **4-pin/PWM**
channels; on a 3-pin/DC channel it offers a fixed percentage only.)

pCUE works around this by closing the loop itself, using an **external handheld tachometer** as the
missing sensor: it trims the fan's power up or down until the meter reads the RPM you asked for.

**The meter is a USB-HID digital tachometer that enumerates as VID `0x1A86` / PID `0xE008`** — a
CH340-class HID bridge. pCUE reads its display data directly (the same 7-segment frame format the
meter drives its own LCD with) and decodes RPM plus the low-battery flag. This is the same meter and
the same decoding used by the Cybenetics lab's Faganas test software, so any unit that presents that
VID/PID and frame format should work.

If your meter enumerates with a different VID/PID it will not be detected. Turn on **Debug log**
(bottom row) and the log lists every HID device it saw, plus the first raw report from the meter,
which is enough to adapt the decoder.

> The tachometer is only needed for the closed-loop RPM hold. Everything else — percentages, fan
> modes, monitoring — works without it.

## Remote control (optional)

Tick **Remote** to expose an HTTP API for scripting or unattended benches.

- With the **Token** box empty, pCUE listens on `127.0.0.1` only.
- Entering a token is what allows access from other machines, and every such request must present
  it. There is deliberately no way to expose fan control on the network unauthenticated.

`GET /` lists the endpoints. `tools/pcue-cli.ps1` is a client that can also discover pCUE instances
on the LAN.

## Building

```powershell
MSBuild pCUE\pCUE.csproj /p:Configuration=Debug
pwsh scripts\Invoke-LocalCI.ps1     # build + UI layout check + packaging
pwsh build\pack-release.ps1         # installer + portable zip into artifacts\
```

## Credits

Commander PRO protocol details come from the open-source reverse-engineering work in
[OpenCorsairLink](https://github.com/audiohacked/OpenCorsairLink) and
[liquidctl](https://github.com/liquidctl/liquidctl). CPU sensors via
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), USB HID via
[HidSharp](https://www.zer7.com/software/hidsharp).

Use at your own risk: this drives real hardware.
