<#
.SYNOPSIS
  Offline end-to-end tests for tools/pcue-cli.ps1 against a stub pCUE server.

.DESCRIPTION
  Spins up an HttpListener stub on loopback that mimics a token-protected pCUE (every
  endpoint 401s without X-pCUE-Token: e2e-token), then drives the real CLI through its
  exit-code contract: 0 success, 1 refused, 2 unreachable, 3 bad usage. Each CLI case runs
  in a CHILD pwsh process, because the CLI's `exit` would otherwise terminate this host.
  No hardware, no bench PC, no network beyond 127.0.0.1.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $root 'tools\pcue-cli.ps1'
$Token = 'e2e-token'

function Get-FreeTcpPort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally { $listener.Stop() }
}

$port = Get-FreeTcpPort

# The stub runs in a CHILD pwsh process (not a job): it blocks in GetContext(), and
# Stop-Job cannot interrupt that block in this environment - but Stop-Process -Force can.
$stubFile = Join-Path ([System.IO.Path]::GetTempPath()) 'pcue-e2e-stub.ps1'
$stubCode = @'
param([int]$Port, [string]$Token)
$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $req = $ctx.Request
        $resp = $ctx.Response
        $resp.ContentType = 'application/json'
        try {
            if (($req.Headers['X-pCUE-Token'] + '') -ne $Token) {
                $resp.StatusCode = 401
                $body = '{"error":"Unauthorized."}'
            } else {
                $path = ($req.Url.AbsolutePath + '').TrimEnd('/')
                if ($path.Length -eq 0) { $path = '/' }
                $resp.StatusCode = 200
                $body = $null
                switch ($path) {
                    '/' { $body = '{"name":"pCUE","version":"1.6.1","protocolVersion":2}' }
                    '/status' {
                        $body = '{"version":"1.6.1","protocolVersion":2,' +
                            '"commander":{"connected":true,"firmware":"1.0.3"},' +
                            '"fans":[{"fan":1,"rpm":1200,"mode":"4-pin","setpoint":45}],' +
                            '"tachometer":{"connected":false,"rpm":null,"batteryLow":false,"assignedFan":null},' +
                            '"hold":{"running":false,"status":"Idle","fan":null,"duty":null,"dutySource":"unknown"},' +
                            '"cpu":{"monitoring":false,"temperature":0,"mhz":0,"load":0}}'
                    }
                    '/log/level' { $body = '{"level":"Info"}' }
                    '/fan/duty' {
                        if ($req.HttpMethod -ne 'POST') { $resp.StatusCode = 405; $body = '{"error":"POST required."}' }
                        else { $body = '{"ok":true}' }
                    }
                    '/screenshot' {
                        $resp.ContentType = 'image/png'
                        $png = New-Object byte[] 6000
                        $resp.ContentLength64 = $png.Length
                        $resp.OutputStream.Write($png, 0, $png.Length)
                    }
                    default { $resp.StatusCode = 404; $body = '{"error":"Unknown endpoint."}' }
                }
            }
            if ($null -ne $body) {
                $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                $resp.ContentLength64 = $bytes.Length
                $resp.OutputStream.Write($bytes, 0, $bytes.Length)
            }
        } catch { }
        finally { try { $resp.Close() } catch { } }
    }
} catch { }
finally { try { $listener.Stop() } catch { } }
'@
Set-Content -Path $stubFile -Value $stubCode -Encoding UTF8
$proc = Start-Process pwsh -ArgumentList @('-NoProfile', '-File', $stubFile, "$port", $Token) `
    -NoNewWindow -PassThru
try {
    # Wait for the stub to listen (it runs in another process; fail fast, not after 10 s timeouts).
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($true) {
        try { $tcp = New-Object Net.Sockets.TcpClient; $tcp.Connect('127.0.0.1', $port); $tcp.Close(); break }
        catch { if ([DateTime]::UtcNow -gt $deadline) { throw "Stub server did not start on port $port." }; Start-Sleep -Milliseconds 100 }
    }

    $script:failures = 0
    function Check-Cli([string]$Name, [string[]]$CliArgs, [int]$ExpectCode, [string]$ExpectOutput = '') {
        $out = & pwsh -NoProfile -File $cli @CliArgs 2>&1 | Out-String
        $code = $LASTEXITCODE
        $ok = ($code -eq $ExpectCode) -and ($ExpectOutput -eq '' -or $out -match $ExpectOutput)
        if ($ok) { Write-Host "PASS $Name" }
        else {
            $script:failures++
            Write-Host "FAIL $Name (exit $code, want $ExpectCode; output: $($out.Trim()))" -ForegroundColor Red
        }
    }

    $srv = @('-Server', "127.0.0.1:$port", '-TimeoutSec', '5')
    Check-Cli 'status renders fans' (@('status') + $srv + @('-Token', $Token)) 0 'pCUE'
    Check-Cli 'status -Json parses' (@('status', '-Json') + $srv + @('-Token', $Token)) 0 '"version": "1.6.1"'
    Check-Cli 'info lists endpoints' (@('info') + $srv + @('-Token', $Token)) 0 'protocolVersion'
    Check-Cli 'duty write ok' (@('duty', '2', '45') + $srv + @('-Token', $Token)) 0 'ok'
    Check-Cli 'loglevel reads' (@('loglevel') + $srv + @('-Token', $Token)) 0 ''
    $shot = Join-Path ([System.IO.Path]::GetTempPath()) 'pcue-e2e-shot.png'
    try { if (Test-Path $shot) { Remove-Item $shot -Force } } catch { }
    Check-Cli 'shot downloads' (@('shot', 'main', $shot) + $srv + @('-Token', $Token)) 0 'KB'
    if (Test-Path $shot) {
        if ((Get-Item $shot).Length -eq 6000) { Write-Host 'PASS shot size' }
        else { $script:failures++; Write-Host 'FAIL shot size' -ForegroundColor Red }
        try { Remove-Item $shot -Force } catch { }
    } else { $script:failures++; Write-Host 'FAIL shot file missing' -ForegroundColor Red }

    Check-Cli 'missing token refused' (@('status') + $srv) 1 'Refused'
    Check-Cli 'bad fan rejected' (@('duty', '9', '45') + $srv + @('-Token', $Token)) 3 'Fan must be 1-6'
    Check-Cli 'bad duty rejected' (@('duty', '2', '101') + $srv + @('-Token', $Token)) 3 'Duty must be 0-100'
    Check-Cli 'unknown command rejected' (@('frobnicate') + $srv + @('-Token', $Token)) 3 'Unknown command'
    $dead = Get-FreeTcpPort
    Check-Cli 'unreachable server' @('status', '-Server', "127.0.0.1:$dead", '-TimeoutSec', '3') 2 'Cannot reach'

    if ($script:failures -gt 0) { throw "$($script:failures) CLI e2e check(s) failed." }
    Write-Host 'CLI e2e: all checks passed (stub server only).' -ForegroundColor Green
} finally {
    try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    try { Remove-Item $stubFile -Force -ErrorAction SilentlyContinue } catch { }
}

# Explicit exit 0: the last child pwsh exits 2 BY DESIGN and $LASTEXITCODE is session-global,
# so without this the success code would leak into the caller (local CI gates on it).
# Failure throws above and never reaches here.
exit 0
