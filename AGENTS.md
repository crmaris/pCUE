# pCUE — agent entry point

**Canonical handover lives in [`CLAUDE.md`](./CLAUDE.md). Read it first and keep it updated** (add a
dated session-log entry there for any change to code/config/deploys/decisions).

Most critical facts (full detail in `CLAUDE.md`):
- WPF, **.NET Framework 4.8**, classic `packages.config` project. Controls a **Corsair Commander PRO**
  over USB-HID (HidSharp). Requires admin.
- **Do not upgrade LibreHardwareMonitorLib past 0.9.4** or **HidSharp past 2.1.0** — both break the
  Commander PRO HID code. (CPU Temp/MHz/Load come from LibreHardwareMonitor.)
- **AssemblyVersion stays 1.1.0.0; AssemblyFileVersion auto-bumps on every _Release_ build** (inline
  MSBuild task) — don't hand-edit it. C# 12 on .NET Framework 4.8 (no nullable-reference annotations).
- Fan numeric box: **≤100 = PWM power %** (whole-percent only, hardware limit), **>100 = fixed RPM**.
- External bench tachometer (VID 0x1A86/PID 0xE008) support lives in `pCUE/Tachometer/HidTachometer.cs`.
