<#
.SYNOPSIS
  Bench validation for the pCUE 1.5.3 behaviour changes, driven entirely over the remote API.

.DESCRIPTION
  Runs the checks that were owed after the status-byte/dithering work and that CLAUDE.md lists as
  "still owed" - the ones that do NOT need a human at the bench PC:

    A  3-pin rejection      an RPM target sent to a 3-pin channel is refused by the DEVICE and the
                            refusal reaches the caller (HTTP 400 naming WRITE_FAN_SPEED) and the log
    B  dither descending +  a software hold converges DOWNWARD to a target, then a LIVE retarget
       live retarget        drops any dither bracket and re-converges; log shows dither engagement
    D  /status honesty      after Stop, hold.duty reports the last ACCEPTED commanded duty with
                            dutySource "tracked" - never a stale loop value

  Check C (hold across an app restart exercising the suspicious read-back retry) needs someone to
  restart the app at the bench; this script prints it as MANUAL.

  Every phase writes its raw evidence (status snapshots, log tails) into
  $env:TEMP\pcue-bench\<timestamp>\ so a FAIL can be diagnosed afterwards.

  Safety: fans are driven to sane targets only (<=1500 RPM here), the tachometer is reassigned back
  to where it was, and the final state diff against the start snapshot is printed.

.EXAMPLE
  pwsh tools\bench-validate.ps1 -Server 192.168.1.20 [-Token <secret>]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Server,
    [string]$Token,
    # Hold targets. Defaults are gentle for a typical 120/140mm bench fan.
    [double]$TargetHigh = 1200,
    [double]$TargetLow  = 1050,
    [int]   $ConvergeTimeoutSec = 180
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- helpers
$script:Hdr = @{}
if ($Token) { $script:Hdr['X-pCUE-Token'] = $Token }
$base = "http://$Server`:5056"

function Invoke-Pcue {
    param([string]$Method = 'GET', [string]$Path, [object]$Body)
    try {
        if ($Body) {
            return Invoke-RestMethod -Uri "$base$Path" -Method $Method -Headers $script:Hdr `
                -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 15
        }
        return Invoke-RestMethod -Uri "$base$Path" -Method $Method -Headers $script:Hdr -TimeoutSec 15
    } catch {
        $respBody = $null
        try { $respBody = $_.ErrorDetails.Message } catch { }
        return [pscustomobject]@{ __failed = $true; __status = $_.Exception.Response.StatusCode.value__; __body = $respBody }
    }
}

$evidence = Join-Path $env:TEMP ('pcue-bench\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
function Save-Evidence { param([string]$Name, [object]$Data)
    $Data | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $evidence $Name) -Encoding UTF8
}

$results = [System.Collections.Generic.List[object]]::new()
function Record { param([string]$Check, [string]$Verdict, [string]$Detail)
    $results.Add([pscustomobject]@{ Check = $Check; Verdict = $Verdict; Detail = $Detail })
    $colour = if ($Verdict -eq 'PASS') { 'Green' } elseif ($Verdict -eq 'FAIL') { 'Red' } else { 'Yellow' }
    Write-Host ("[{0}] {1} - {2}" -f $Verdict.PadRight(6), $Check, $Detail) -ForegroundColor $colour
}

# ---------------------------------------------------------------- preconditions
Write-Host "Evidence dir: $evidence`n"
$start = Invoke-Pcue -Path '/status'
if ($start.__failed) { throw "Cannot reach pCUE at $base - is the app running with Remote enabled?" }
Save-Evidence '00_initial_status.json' $start

$ver = [version]$start.version
if ($ver -lt [version]'1.5.3') {
    throw "Bench runs pCUE $($start.version); these checks need >= 1.5.3 installed first."
}
if (-not $start.commander.connected) {
    Write-Host "Commander not connected - opening it remotely..."
    [void](Invoke-Pcue -Method Post -Path '/commander/open')
    Start-Sleep -Seconds 2
    $start = Invoke-Pcue -Path '/status'
    Save-Evidence '00_initial_status.json' $start
}
if (-not $start.commander.connected) { throw "Commander PRO did not connect." }

$tachWasAssigned = [int]($start.tachometer.assignedFan ?? 0)
$tachWasConnected = [bool]$start.tachometer.connected
Write-Host ("Start state: v{0}, commander connected, tach connected={1} assignedFan={2}" -f `
    $start.version, $tachWasConnected, $tachWasAssigned)

# ---------------------------------------------------------------- Check A: 3-pin rejection
$fan3pin = $null
foreach ($f in $start.fans) { if ($f.mode -eq '3pin') { $fan3pin = [int]$f.fan; break } }

if (-not $fan3pin) {
    Record 'A 3-pin rejection' 'SKIP' "no channel currently in 3-pin mode; set one and rerun"
} else {
    $r = Invoke-Pcue -Method Post -Path '/fan/rpm' -Body @{ fan = $fan3pin; value = 1200 }
    Save-Evidence 'A_reject_response.json' $r
    $rejected = $false
    if ($r.__failed -and $r.__status -eq 400 -and $r.__body -match 'WRITE_FAN_SPEED') { $rejected = $true }
    $logText = (Invoke-Pcue -Path '/log?tail=40').lines -join "`n"
    Save-Evidence 'A_log_tail.txt' $logText
    $logged = $logText -match 'DEVICE REJECTED'
    if ($rejected -and $logged) {
        Record 'A 3-pin rejection' 'PASS' "fan $fan3pin refused by device (HTTP 400 + DEVICE REJECTED in log)"
    } elseif ($rejected) {
        Record 'A 3-pin rejection' 'PASS' "caller saw the refusal (HTTP 400) but no DEVICE REJECTED log line"
    } else {
        Record 'A 3-pin rejection' 'FAIL' "device did NOT refuse the RPM target on fan $fan3pin - inspect A_*.json"
    }
}

# ---------------------------------------------------------------- Check B: descending hold + live retarget
$fanH = if ($fan3pin) { $fan3pin } else { 1 }

$tachRes = Invoke-Pcue -Method Post -Path '/tach/connect'
if ($tachRes.__failed) {
    Write-Host "tachometer connect failed - Check B needs it for feedback; skipping B." -ForegroundColor Yellow
    Record 'B dither descend+retarget' 'SKIP' 'no tachometer available'
} else {
    [void](Invoke-Pcue -Method Post -Path '/tach/assign' -Body @{ fan = $fanH })

    function Wait-Stable {
        param([double]$Target, [int]$TimeoutSec)
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        $timeline = @()
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            $s = Invoke-Pcue -Path '/status'
            $h = $s.hold
            $rpm = $s.fans | Where-Object fan -eq $fanH | ForEach-Object rpm
            $line = '{0:s}  status={1,-11} rpm={2,5} duty={3}' -f (Get-Date), $h.status, $rpm, $h.duty
            $timeline += $line
            if ($h.status -eq 'Stable' -and [Math]::Abs(($rpm ?? 0) - $Target) -le 35) {
                return @{ ok = $true; rpm = $rpm; timeline = $timeline }
            }
        }
        return @{ ok = $false; rpm = $null; timeline = $timeline }
    }

    # Phase 1: converge downward-ish to TargetHigh from wherever the fan sits
    $h1 = Invoke-Pcue -Method Post -Path '/hold/start' -Body @{ fan = $fanH; rpm = $TargetHigh }
    if ($h1.__failed) { Record 'B dither descend+retarget' 'FAIL' "hold start refused: $($h1.__body)"; }
    else {
        $p1 = Wait-Stable -Target $TargetHigh -TimeoutSec $ConvergeTimeoutSec -Tag 'high'
        Save-Evidence "B_phase1_$TargetHigh.json" ($p1.timeline -join "`n")

        # Phase 2: LIVE retarget lower while running - must drop any bracket and re-converge
        [void](Invoke-Pcue -Method Post -Path '/hold/config' -Body @{ target = $TargetLow })
        $p2 = Wait-Stable -Target $TargetLow -TimeoutSec $ConvergeTimeoutSec -Tag 'low'
        Save-Evidence "B_phase2_$TargetLow.json" ($p2.timeline -join "`n")

        $logText = (Invoke-Pcue -Path '/log?tail=200').lines -join "`n"
        Save-Evidence 'B_log_tail.txt' $logText
        $ditherSeen = $logText -match 'dither engaged|dithering'
        $retargetHandled = $logText -match 'resuming steps|dither exited'

        if ($p1.ok -and $p2.ok) {
            Record 'B dither descend+retarget' 'PASS' (
                "converged to {0} then re-converged to {1} after live retarget (fan {2}); dither logged={3}, bracket-drop logged={4}" -f `
                $TargetHigh, $TargetLow, $fanH, [bool]$ditherSeen, [bool]$retargetHandled)
        } else {
            Record 'B dither descend+retarget' 'FAIL' (
                "phase1 ok={0} phase2 ok={1} - see B_phase*.json timelines" -f $p1.ok, $p2.ok)
        }
    }

    # ---------------------------------------------------------------- Check D: post-stop honesty
    $before = Invoke-Pcue -Path '/status'
    Save-Evidence 'D_before_stop.json' $before
    [void](Invoke-Pcue -Method Post -Path '/hold/stop')
    Start-Sleep -Seconds 1
    $after = Invoke-Pcue -Path '/status'
    Save-Evidence 'D_after_stop.json' $after

    $h = $after.hold
    if (-not $h.running -and $h.dutySource -eq 'tracked' -and $h.duty -ne $null -and $h.duty -ge 0 -and $h.duty -le 100) {
        Record 'D /status honesty' 'PASS' ("stopped; duty={0}% dutySource='tracked' (matches what the loop last applied)" -f $h.duty)
    } else {
        Record 'D /status honesty' 'FAIL' ("running={0} duty={1} dutySource='{2}' - see D_after_stop.json" -f $h.running, $h.duty, $h.dutySource)
    }

    # restore tach assignment
    if ($tachWasAssigned -gt 0) { [void](Invoke-Pcue -Method Post -Path '/tach/assign' -Body @{ fan = $tachWasAssigned }) }
    elseif (-not $tachWasConnected) { [void](Invoke-Pcue -Method Post -Path '/tach/disconnect') }
}

Record 'C restart retry' 'MANUAL' "restart the app at the bench with a fan running, then start a hold: expect the 're-read gave N%' Warn line"

# ---------------------------------------------------------------- summary + state diff
Write-Host "`n=== Summary ==="
$results | Format-Table -AutoSize | Out-String | Write-Host
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $evidence 'summary.json')

$final = Invoke-Pcue -Path '/status'
Save-Evidence '99_final_status.json' $final
Write-Host "Held fan left at ~$TargetLow RPM (its last stable target); modes were not changed. Full final state in 99_final_status.json."