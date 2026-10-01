# Shared cooling controller contract

`CorsairCore.cs` and `CoolingUsbDiscovery.cs` are independently authored, MIT-licensed source. They compile with C# 5 / .NET Framework 4.8 and HidSharp 2.1.0 or 2.6.4. The canonical files live in pCUE-Light `shared/`; full pCUE and Case Tests must carry byte-identical copies and the same `CoreBackendTests.cs` conformance suite. Keep each application's pinned dependencies. Never port a protocol by treating it as Commander PRO or by changing its six-channel bench arrays.

## CORE / CORE XT

| Property | CORE | CORE XT |
|---|---|---|
| VID:PID | 1B1C:0C1C | 1B1C:0C2A |
| Protocol outputs | EXT/AIO at 0; fans at 1–6 | Fans at 0–5 |
| Identity | SHA256 of kind + normalized exact HID path | Same |
| Control | Fixed whole percentages, 0–100 at driver layer | Same |
| Capture | Selected mask, full hardware modes and fixed percentages | Same |
| Restore | Merge selected captured values into latest full tables; verify | Same |

The app layer requires explicit output opt-in, identifies Fan/Pump by operator choice, validates its permitted minimum and percentage, and saves recovery atomically BEFORE calling Apply. Discovery opens no device and sets no speed. Reads/captures wake the device and return it to hardware mode but send no setpoint writes. Only interface zero and known HID report lengths are accepted. HidSharp Windows report ID is stripped once. Every transaction checks command echo/status, exact count and data type. Firmware endpoint candidates are selected only by returned type tags: modes 03 and percentages 04. Unknown layout, unsupported selected mode, missing motor, rejected write or wrong readback must block control. Unselected settings and stored curves are preserved. No LED data, curve data, wiring modes, software speed defaults or flash-save command is sent.

Use `Read()` for capabilities/telemetry; `Capture(mask)` for baseline; `Apply(desired, previous, writeAllowed)` for selected changes (`-1` means unmanaged); and `Restore(baseline)` for recovery. Supply the current session check to Apply and re-check it before each write. A partial write/cancel keeps the saved baseline. The app must serialize calls and retain recovery until all selected devices restore. `previous` detects another application's changes to managed outputs. The shared global Corsair mutex only coordinates applications that honor it; close competing control utilities. Wake/sleep changes the controller's global operating mode, so concurrent vendor lighting/control software cannot be guaranteed unaffected.

The app MUST enumerate CORE/XT metadata without opening/waking unselected controllers. Poll only the selected exact devices; close deselected handles. Light implements this in Controllers.MayMonitor and Engine discovery/selection. Full pCUE waits for the operator to choose Connect. Case Tests must enforce the same boundary before polling. Reading a controller is not equivalent to a passive USB metadata scan.

CORE/XT support is experimental and has simulated protocol verification, not physical validation. liquidctl currently labels this family broken. Do not claim every firmware, actual pump response, sleep/resume, thermal stress or electrical safety has been verified. Commander ST (0C32) is excluded. CORE’s EXT pump is included only when its firmware exposes supported mode and percentage tables. Target RPM, DC/PWM wiring changes and acquisition leases are NOT capabilities of this driver; the original Commander PRO bench workflow retains them separately.

## iCUE LINK feasibility

Public FanControl.CorsairLink describes the system hub 1B1C:0C3F, 512-byte protocol reports (Windows descriptor may include report ID), device topology, temperature and speed telemetry, and software percentages. Its framing and topology differ from CORE. A known device's model plus stable ASCII device ID must identify its output; enumeration index cannot survive device-chain changes. Hub channel zero and unknown device types must not become motors. Pumps require explicit identification and limits.

Functional research: command prefix 00/01 after report ID; wake/sleep 01 03 00 02/01, firmware 02 13, endpoint open 0D, close 05, read 08, write 06. Topology endpoint 36/type 21, RPM 17/type 25, temperatures 21/type 10, software percentage 18/type 07. Topology is chunked and variable length; model/variant/ASCII-ID-length fields and every response/status boundary need bounds checks. Never reuse CORE framing, output counts or hardware-table endpoints for LINK.

The public implementation does NOT provide a verified capture/restore contract for the hub's existing software setpoints and hardware profiles. Its automatic 50% fan / 100% pump defaults are not the prior configuration. No LINK driver is enabled in these apps yet. Implementing native write control requires actual protocol traces proving per-device baseline capture, selected-only writes, acknowledgements, real RPM response, and exact restoration, including disconnect/topology-change behavior. Do not invent those commands or ship monitor-only placeholders as working control. be quiet support also awaits its owner session's verified physical response and restore data.

Sources (functional references; no third-party driver source/binary copied):
- [liquidctl protocol](https://github.com/liquidctl/liquidctl/blob/main/docs/developer/protocol/commander_core.md)
- [liquidctl driver and support status](https://github.com/liquidctl/liquidctl/blob/main/liquidctl/driver/commander_core.py)
- [FanControl CORE implementation](https://github.com/EvanMulawski/FanControl.CorsairLink/blob/main/src/devices/commander_core/CommanderCoreDevice.cs)
- [FanControl LINK hub implementation](https://github.com/EvanMulawski/FanControl.CorsairLink/blob/main/src/devices/icue_link/ICueLinkHubDevice.cs)

## Required parity check

Before releasing any of the three apps, compare SHA256 of BOTH shared `.cs` files against canonical pCUE-Light and run the same conformance test suite with the consuming app's dependencies. Record hashes and test evidence in the app handover. Updating one copy creates a parity gap until the other two adopt it; explicitly report pending apps. Protocol changes belong in this source and conformance suite first, rather than in separate UI-specific drivers.

Set `shared/* text eol=lf` and the app's conformance-file path `text eol=lf` in `.gitattributes`. These byte hashes include line endings; Windows autocrlf must not change them. `shared/Test-SharedSource.ps1` checks the committed manifest and can compare against a local canonical root.
