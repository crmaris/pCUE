# Shared cooling controller contract

The controller sources in this folder are owner-authored, MIT-licensed source. They compile with C# 5 / .NET Framework 4.8 and HidSharp 2.1.0 or 2.6.4. Canonical files live in pCUE-Light `shared/`; consuming applications must carry byte-identical copies and the same CORE/be quiet conformance suites. Keep each application's pinned dependencies. Never port a protocol by treating it as Commander PRO or by changing its six-channel bench arrays. `INativeCoolingController` provides Read/Capture/Apply/Restore and keeps saved recovery compatible across the different UIs.

## be quiet IO

The owner's Case Tests investigation physically verified pump and fan-bank response and restoration on IO bridge 373F:0010/MI00, nested model 8003 revision 1, one MCU, firmware bytes 37 00 00 00 (vendor display 0.0.37). The Light/full ports have software simulations only. No private serials, captures or lab measurements are embedded. Other interfaces, LCD PID 0016, models, revisions or firmware are rejected.

Windows reports are 65 bytes: zero report ID followed by 64 protocol bytes. CRC16 (reflected A001, init FFFF, little-endian trailer) covers the first 62 wire bytes. Requests use exact allocated active bridge/child sessions, nonzero counters, status/echo matching, zero padding and both outer ACK plus routed child reply. Native unbuffered overlapped WriteFile sends each output immediately; owned buffers/events remain allocated until OS completion/cancellation finishes. That final OS cancellation completion can block on a defective driver.

Metadata scans never open unselected devices. A chosen controller verifies its exact bridge path/serial and discovers the nested cooler serial before reading settings. Applications remember the child serial as well as the bridge identity. IO Center and its service must release the interface; wait six seconds for session expiry before connecting. The app refuses a competing vendor process and never stops services itself.

Protocol control index 1 is pump, 2 is one fan bank, 3 is global mode (0 internal/fallback, 1 software). Since software mode affects both, claim the whole pump+bank pair, with explicit Pump/Fan role confirmation. Capture pump/bank duty, mode and child identity; save recovery atomically before writes. Establish pump100, then fan target, then software mode, then a lower commissioned manual pump target if requested. Pump commands accept 25–100%; retain pump100 throughout tests/settling/pauses. The transport bank range is 0–100%; this does not certify physical response over that entire range. Unchanged profile ticks send no repeated setpoints. Every normal write checks its current lease immediately before sending and verifies readback; partial cancellation retains recovery.

Read-only samples: channel0 coolant /10 C, 1 pumpRPM, 2 bankRPM, 3 condition/error (not another fan). Each expires after three monotonic seconds. Zero and missing are distinct. PumpRPM >0, bankRPM present, fresh error0 and valid settings are required for normal control. EOF, identity ambiguity or session errors invalidate feedback. Both sessions have one-second heartbeats. While claimed, expired application lease, stale feedback or a fault latches control off and attempts pump100+bank100+software mode using its existing exclusive handle. Heartbeats continue only after a verified full-speed park. USB/session failure stops that park/heartbeat; physical fallback on expiry has not been universally validated.

Stop/restore verifies the exact child, restores the captured bank duty and mode, and deliberately leaves pump100 rather than its captured lower value. Stale telemetry does not prevent these known recovery writes. A failed handle may reconnect only the same bridge+child after session expiry. The app must retain recovery/ownership until all writes/readbacks succeed; no explicit SessionClose/ForceStateChange command is implemented. This cooperative safety worker is not an independent external watchdog: another process cannot open MI00 while this owner holds it. A whole OS hang or stuck native I/O cannot be claimed covered.

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

The public implementation does NOT provide a verified capture/restore contract for the hub's existing software setpoints and hardware profiles. Its automatic 50% fan / 100% pump defaults are not the prior configuration. No LINK driver is enabled in these apps yet. Native write control requires traces proving per-device baseline capture, selected-only writes, acknowledgements, real RPM response and restoration, including disconnect/topology changes. Do not invent commands or ship monitor-only placeholders as working control.

Sources (functional references; no third-party driver source/binary copied):
- [liquidctl protocol](https://github.com/liquidctl/liquidctl/blob/main/docs/developer/protocol/commander_core.md)
- [liquidctl driver and support status](https://github.com/liquidctl/liquidctl/blob/main/liquidctl/driver/commander_core.py)
- [FanControl CORE implementation](https://github.com/EvanMulawski/FanControl.CorsairLink/blob/main/src/devices/commander_core/CommanderCoreDevice.cs)
- [FanControl LINK hub implementation](https://github.com/EvanMulawski/FanControl.CorsairLink/blob/main/src/devices/icue_link/ICueLinkHubDevice.cs)

## Required parity check

Before releasing, check every source/test in `source-manifest.json` against canonical pCUE-Light and run both shared conformance suites with the consuming app's dependencies. Record hashes/evidence in the handover. Updating one copy creates a parity gap until the other apps adopt it; report pending apps. Protocol changes belong here first, rather than in separate UI-specific drivers. Baseline parity still requires adoption of other existing controller families (OCTO/mainboard) where an app lacks them; matching these new USB files alone does not establish complete three-app parity.

Set `shared/* text eol=lf` and the app's conformance-file path `text eol=lf` in `.gitattributes`. These byte hashes include line endings; Windows autocrlf must not change them. `shared/Test-SharedSource.ps1` checks the committed manifest and can compare against a local canonical root.
