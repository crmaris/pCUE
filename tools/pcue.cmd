@echo off
rem Thin shim so the CLI is "pcue <command>" rather than a .ps1 path. Prefers PowerShell 7 when
rem it is installed, falls back to Windows PowerShell, and passes the exit code straight back
rem through so scripts can branch on it.
setlocal
set "PSEXE=powershell"
where pwsh >nul 2>&1 && set "PSEXE=pwsh"
%PSEXE% -NoProfile -ExecutionPolicy Bypass -File "%~dp0pcue-cli.ps1" %*
exit /b %ERRORLEVEL%
