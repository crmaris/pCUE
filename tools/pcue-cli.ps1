<#
.SYNOPSIS
  Command-line client for pCUE's remote-control API. Finds pCUE instances on the LAN and drives them.

.DESCRIPTION
  pCUE must be started with remote control enabled:

      pCUE.exe --remote --debug                                   # loopback only
      pCUE.exe --remote-prefix=http://+:5056/ --remote-token=SECRET --debug   # LAN

  On the bench PC, the LAN form needs the port opened once (elevated):

      New-NetFirewallRule -DisplayName "pCUE remote" -Direction Inbound -Protocol TCP -LocalPort 5056 -Action Allow
      New-NetFirewallRule -DisplayName "pCUE discovery" -Direction Inbound -Protocol UDP -LocalPort 5057 -Action Allow

  Without a token, pCUE refuses every non-loopback request even if the prefix is wide open - so a
  LAN deployment MUST set -Token.

.EXAMPLE
  .\pcue-cli.ps1 discover
  .\pcue-cli.ps1 status  -Server http://192.168.1.50:5056 -Token SECRET
  .\pcue-cli.ps1 log     -Tail 100 -Server http://192.168.1.50:5056 -Token SECRET
  .\pcue-cli.ps1 debug   -Server ...            # switch the log to Debug level
  .\pcue-cli.ps1 duty    -Fan 1 -Value 60 -Server ...
  .\pcue-cli.ps1 rpm     -Fan 1 -Value 900 -Server ...
  .\pcue-cli.ps1 mode    -Fan 1 -Mode 3pin -Server ...
  .\pcue-cli.ps1 hold    -Fan 1 -Value 800 -Server ...
  .\pcue-cli.ps1 stop    -Server ...
  .\pcue-cli.ps1 watch   -Server ...            # live status until Ctrl-C
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('discover','status','log','debug','info','duty','rpm','mode','hold','stop',
                 'open','close','cpu-start','cpu-stop','tach-connect','tach-disconnect','assign',
                 'reset','watch','help')]
    [string]$Command = 'help',

    [string]$Server = 'http://127.0.0.1:5056',
    [string]$Token,
    [int]$Fan,
    [int]$Value,
    [string]$Mode,
    [int]$Tail = 200,
    [int]$IntervalMs = 1000,
    [int]$DiscoverTimeoutMs = 1500
)

$ErrorActionPreference = 'Stop'

function Get-Headers {
    if ($Token) { return @{ 'X-pCUE-Token' = $Token } }
    return @{}
}

function Invoke-Api {
    param([string]$Path, [string]$Method = 'Get')
    $uri = "$($Server.TrimEnd('/'))$Path"
    try {
        return Invoke-RestMethod -Uri $uri -Method $Method -Headers (Get-Headers) -TimeoutSec 20
    } catch {
        # The API returns a JSON body on errors; surface it instead of a bare 400.
        $detail = $_.ErrorDetails.Message
        if ($detail) { throw "pCUE API error: $detail" }
        throw
    }
}

function Find-Instances {
    # Send the probe to the SUBNET-DIRECTED broadcast address of every IPv4 interface, not just
    # 255.255.255.255. On a multi-homed box (WSL, Hyper-V switches, VPN adapters) a single
    # 255.255.255.255 datagram leaves via one interface only - usually the wrong one - so the bench
    # PC never hears it.
    $udp = New-Object System.Net.Sockets.UdpClient
    try {
        $udp.EnableBroadcast = $true
        $udp.Client.ReceiveTimeout = $DiscoverTimeoutMs
        $probe = [Text.Encoding]::UTF8.GetBytes('PCUE_DISCOVER')

        $targets = @([Net.IPAddress]::Broadcast)
        foreach ($a in Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue) {
            if ($a.IPAddress -like '127.*' -or $a.IPAddress -like '169.254.*') { continue }
            try {
                $ipBytes   = ([Net.IPAddress]::Parse($a.IPAddress)).GetAddressBytes()
                $maskBits  = [uint32]$a.PrefixLength
                $maskValue = if ($maskBits -eq 0) { 0 } else { [uint32]::MaxValue -shl (32 - $maskBits) }
                $maskBytes = [BitConverter]::GetBytes([uint32]$maskValue)
                if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($maskBytes) }
                # host bits all 1 = the directed broadcast for that subnet
                $bcast = 0..3 | ForEach-Object { [byte]($ipBytes[$_] -bor (-bnot $maskBytes[$_] -band 0xFF)) }
                $targets += [Net.IPAddress]::new([byte[]]$bcast)
            } catch { }
        }

        foreach ($t in ($targets | Sort-Object -Property IPAddressToString -Unique)) {
            try { [void]$udp.Send($probe, $probe.Length, (New-Object Net.IPEndPoint $t, 5057)) } catch { }
        }

        $found = @()
        $deadline = [DateTime]::UtcNow.AddMilliseconds($DiscoverTimeoutMs)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $remote = New-Object Net.IPEndPoint ([Net.IPAddress]::Any), 0
                $bytes = $udp.Receive([ref]$remote)
                $found += ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
            } catch { break }   # timeout
        }
        return $found | Sort-Object -Property url -Unique
    } finally { $udp.Close() }
}

switch ($Command) {
    'help' { Get-Help $PSCommandPath -Detailed; break }

    'discover' {
        $list = Find-Instances
        if (-not $list -or $list.Count -eq 0) {
            Write-Warning "No pCUE instances answered. Check that pCUE was started with --remote and that UDP 5057 is open."
        } else {
            $list | Select-Object app, version, host, url, requiresToken | Format-Table -AutoSize
        }
        break
    }

    'status' { Invoke-Api '/status' | ConvertTo-Json -Depth 6; break }
    'info'   { Invoke-Api '/'       | ConvertTo-Json -Depth 6; break }

    'log' {
        $r = Invoke-Api "/log?tail=$Tail"
        Write-Host "level=$($r.level)  file=$($r.file)" -ForegroundColor Cyan
        $r.lines
        break
    }

    'debug' { Invoke-Api '/log/level?value=debug' | ConvertTo-Json; break }

    'duty'  { Invoke-Api "/fan/duty?fan=$Fan&value=$Value" 'Post' | ConvertTo-Json -Depth 6; break }
    'rpm'   { Invoke-Api "/fan/rpm?fan=$Fan&value=$Value"  'Post' | ConvertTo-Json -Depth 6; break }
    'mode'  { Invoke-Api "/fan/mode?fan=$Fan&value=$Mode"  'Post' | ConvertTo-Json -Depth 6; break }
    'hold'  { Invoke-Api "/hold/start?fan=$Fan&rpm=$Value" 'Post' | ConvertTo-Json -Depth 6; break }
    'stop'  { Invoke-Api '/hold/stop'        'Post' | ConvertTo-Json -Depth 6; break }
    'open'  { Invoke-Api '/commander/open'   'Post' | ConvertTo-Json -Depth 6; break }
    'close' { Invoke-Api '/commander/close'  'Post' | ConvertTo-Json -Depth 6; break }
    'cpu-start' { Invoke-Api '/cpu/start' 'Post' | ConvertTo-Json -Depth 6; break }
    'cpu-stop'  { Invoke-Api '/cpu/stop'  'Post' | ConvertTo-Json -Depth 6; break }
    'tach-connect'    { Invoke-Api '/tach/connect'    'Post' | ConvertTo-Json -Depth 6; break }
    'tach-disconnect' { Invoke-Api '/tach/disconnect' 'Post' | ConvertTo-Json -Depth 6; break }
    'assign' { Invoke-Api "/tach/assign?fan=$Fan" 'Post' | ConvertTo-Json -Depth 6; break }
    'reset'  { Invoke-Api '/reset' 'Post' | ConvertTo-Json -Depth 6; break }

    'watch' {
        Write-Host "Watching $Server - Ctrl-C to stop." -ForegroundColor Cyan
        while ($true) {
            $s = Invoke-Api '/status'
            $fans = ($s.fans | ForEach-Object { "#$($_.fan) $($_.rpm)rpm/$($_.mode)" }) -join '  '
            "{0}  cmdr={1} hold={2}@{3}%  {4}" -f (Get-Date -Format 'HH:mm:ss'),
                $s.commander.connected, $s.hold.status, $s.hold.duty, $fans
            Start-Sleep -Milliseconds $IntervalMs
        }
        break
    }
}
