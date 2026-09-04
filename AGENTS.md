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

## Workspace hygiene (owner directive 2026-09-04)

A scheduled job in `E:\All projects\Workspace Maintenance` prunes, without asking, `bin/ obj/ .vs/
packages/ node_modules/ .venv/ __pycache__/` older than 7 days and agent scratch (`.codex-tmp/
*-temp*/ dotnet-temp*/ NuGetScratch/`) older than 7 days. It never deletes CI / validation output
under `artifacts\` — **that is this session's job**: before the handover update, delete the validation
checkouts, `local-ci` runs, `terra-*` / `*-temp*` folders, staging trees and test packages you created
and no longer need, plus anything there older than 14 days that this document does not cite by path.
Keep only what this document names as provenance, rollback or evidence, and the newest release package.
Scratch goes in `.codex-tmp\` (Codex) or the session scratchpad (Claude), never in `artifacts\`.
`Remove-Item` is blocked on these paths — use `[System.IO.Directory]::Delete($path, $true)`.
Full rules and the protected-path registry: `E:\All projects\Workspace Maintenance\CLAUDE.md`.