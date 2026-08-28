<#
.SYNOPSIS
  Command-line control for pCUE. Covers every remote endpoint the app exposes.

.DESCRIPTION
  pCUE's HTTP API is the same surface the GUI drives, so anything you can do by clicking you can
  do from here - set duties, switch fan modes, run the closed-loop RPM hold, tune the loop, read
  the log, grab a screenshot of the running UI.

  The app must be running with Remote ticked (bottom strip). Loopback needs no token; any other
  machine does. Put it in PCUE_TOKEN rather than typing it every time - this script deliberately
  does NOT write it to disk, matching pCUE's in-app remote client.

  Set PCUE_SERVER to point at a bench box by default. With neither that nor -Server, the CLI
  tries localhost first and then asks the LAN (UDP 5057) who is listening.

.PARAMETER Command
  What to do. Run "pcue-cli.ps1 commands" for the full list with examples.

.PARAMETER Server
  host or host:port to talk to. Default: $env:PCUE_SERVER, else localhost, else whatever answers
  a LAN discovery probe.

.PARAMETER Token
  Shared secret for non-loopback access. Default: $env:PCUE_TOKEN.

.PARAMETER Json
  Emit raw JSON instead of the formatted view. Use this when scripting.

.EXAMPLE
  pcue-cli.ps1 status

.EXAMPLE
  pcue-cli.ps1 duty 2 45

.EXAMPLE
  pcue-cli.ps1 hold 2 1200

.EXAMPLE
  $env:PCUE_SERVER='192.168.1.20'; $env:PCUE_TOKEN='secret'; pcue-cli.ps1 watch
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = 'help',

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Rest,

    [string]$Server,
    [string]$Token = $env:PCUE_TOKEN,
    [int]$Port = 5056,
    [int]$TimeoutSec = 10,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

# Exit codes are part of the interface: a script driving a bench needs to tell "pCUE said no"
# apart from "pCUE was not there at all".
$EXIT_OK = 0; $EXIT_ERROR = 1; $EXIT_UNREACHABLE = 2; $EXIT_USAGE = 3

$script:Base = $null

function Die([string]$Message, [int]$Code) {
    Write-Host $Message -ForegroundColor Red
    exit $Code
}

function Find-Pcue([int]$TimeoutMs = 1500) {
    # The beacon is a passive responder: it answers PCUE_DISCOVER with app/version/host/url and
    # whether a token is needed. It never returns the token itself.
    $found = @()
    $udp = $null
    try {
        $udp = New-Object Net.Sockets.UdpClient
        $udp.EnableBroadcast = $true
        $udp.Client.ReceiveTimeout = $TimeoutMs
        $probe = [Text.Encoding]::UTF8.GetBytes('PCUE_DISCOVER')
        [void]$udp.Send($probe, $probe.Length, (New-Object Net.IPEndPoint([Net.IPAddress]::Broadcast, 5057)))
        $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
            try { $found += ([Text.Encoding]::UTF8.GetString($udp.Receive([ref]$ep)) | ConvertFrom-Json) }
            catch { break }
        }
    } catch { } finally { if ($udp) { $udp.Close() } }
    return $found
}

function Resolve-Base {
    # Cached: 'watch' calls this every tick, and re-broadcasting a discovery probe every two
    # seconds would be both slow and rude to the network.
    if ($script:Base) { return $script:Base }

    $target = $Server
    if (-not $target -and $env:PCUE_SERVER) { $target = $env:PCUE_SERVER }

    if ($target) {
        if ($target -notmatch '^https?://') { $target = "http://$target" }
        if ($target -notmatch ':\d+(/|$)') { $target = "$($target):$Port" }
        $script:Base = $target.TrimEnd('/')
        return $script:Base
    }

    # Nothing specified: the local app is overwhelmingly the common case, and needs no token.
    try {
        Invoke-RestMethod -Uri "http://127.0.0.1:$Port/status" -TimeoutSec 2 | Out-Null
        $script:Base = "http://127.0.0.1:$Port"
        return $script:Base
    } catch { }

    $hits = @(Find-Pcue)
    if ($hits.Count -eq 1) {
        Write-Host "Using $($hits[0].host) ($($hits[0].url))" -ForegroundColor DarkGray
        $script:Base = ([string]$hits[0].url).TrimEnd('/')
        return $script:Base
    }
    if ($hits.Count -gt 1) {
        Write-Host 'More than one pCUE answered - name one with -Server:' -ForegroundColor Yellow
        $hits | ForEach-Object { Write-Host ("  {0,-18} {1}" -f $_.host, $_.url) }
        exit $EXIT_USAGE
    }
    Die 'No pCUE found. Start it and tick Remote (bottom strip), or pass -Server.' $EXIT_UNREACHABLE
}

function Invoke-Api([string]$Path, [string]$Method = 'Get', [object]$Body = $null) {
    $base = Resolve-Base
    $headers = @{}
    if ($Token) { $headers['X-pCUE-Token'] = $Token }
    try {
        $request = @{
            Uri = "$base$Path"
            Method = $Method
            Headers = $headers
            TimeoutSec = $TimeoutSec
        }
        if ($null -ne $Body) {
            $request.Body = $Body | ConvertTo-Json -Depth 6 -Compress
            $request.ContentType = 'application/json'
        }
        return Invoke-RestMethod @request
    } catch {
        # Everything hinges on whether a response came back at all. PowerShell 7 fills in
        # ErrorDetails even when the connection was refused, so testing that first blames pCUE
        # for saying something when nothing was listening - and reports "error" for what is
        # really "unreachable", which is the one distinction a bench script needs.
        $response = $null
        try { $response = $_.Exception.Response } catch { }

        if (-not $response) {
            Die "Cannot reach $base - $($_.Exception.Message)" $EXIT_UNREACHABLE
        }

        $code = 0
        try { $code = [int]$response.StatusCode } catch { }
        if ($code -eq 401) {
            Die "Refused by $base. That pCUE wants a token - set PCUE_TOKEN or pass -Token." $EXIT_ERROR
        }

        # The app answers errors as JSON with an 'error' field. Surface that rather than the
        # generic status line, which says nothing useful.
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            $detail = $_.ErrorDetails.Message
            try {
                $parsed = $detail | ConvertFrom-Json
                if ($parsed.error) { Die "pCUE: $($parsed.error)" $EXIT_ERROR }
            } catch { }
            Die "pCUE: $detail" $EXIT_ERROR
        }
        Die "pCUE returned HTTP $code." $EXIT_ERROR
    }
}

function Get-FanArg([string]$Value, [switch]$AllowAll, [switch]$AllowNone) {
    if ($AllowAll -and $Value -eq 'all') { return 'all' }
    if ($AllowNone -and $Value -eq 'none') { return 0 }
    $n = 0
    if (-not [int]::TryParse($Value, [ref]$n) -or $n -lt 1 -or $n -gt 6) {
        $extra = ''
        if ($AllowAll) { $extra += ' or all' }
        if ($AllowNone) { $extra += ' or none' }
        Die "Fan must be 1-6$extra (got '$Value')." $EXIT_USAGE
    }
    return $n
}

function Need([int]$Count, [string]$Usage) {
    if (-not $Rest -or $Rest.Count -lt $Count) { Die "Usage: pcue-cli.ps1 $Usage" $EXIT_USAGE }
}

function Show-Status($s) {
    if ($Json) { $s | ConvertTo-Json -Depth 8; return }
    $fw = ''
    if ($s.commander.firmware) { $fw = "  fw $($s.commander.firmware)" }
    $conn = 'not connected'
    if ($s.commander.connected) { $conn = 'connected' }

    Write-Host ''
    Write-Host ("  pCUE {0}   Commander: {1}{2}" -f $s.version, $conn, $fw) -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  Fan   RPM     Mode          Setpoint'
    Write-Host '  ------------------------------------'
    foreach ($f in $s.fans) {
        Write-Host ("  {0,-5} {1,-7} {2,-13} {3}" -f $f.fan, $f.rpm, $f.mode, $f.setpoint)
    }

    $t = $s.tachometer
    $tachState = 'not connected'
    if ($t.connected) { $tachState = 'connected' }
    $tachRpm = ''
    if ($null -ne $t.rpm) { $tachRpm = "  $($t.rpm) rpm" }
    $tachFan = '  (unassigned)'
    if ($t.assignedFan) { $tachFan = "  -> fan $($t.assignedFan)" }
    $batt = ''
    $battColour = 'Gray'
    if ($t.batteryLow) { $batt = '   BATTERY LOW'; $battColour = 'Yellow' }

    Write-Host ''
    Write-Host ("  Tach:  {0}{1}{2}{3}" -f $tachState, $tachRpm, $tachFan, $batt) -ForegroundColor $battColour

    $h = $s.hold
    $holdFan = ''
    if ($h.fan) { $holdFan = "  fan $($h.fan)" }
    $holdDetail = ''
    if ($h.running) { $holdDetail = "  target $($h.target) rpm, duty $($h.duty)%" }
    Write-Host ("  Hold:  {0}{1}{2}" -f $h.status, $holdFan, $holdDetail)

    if ($s.cpu.monitoring) {
        Write-Host ("  CPU:   {0} C  {1} MHz  {2} %" -f $s.cpu.temperature, $s.cpu.mhz, $s.cpu.load)
    }
    Write-Host ''
}

function Out-Result($r) {
    if ($Json) { $r | ConvertTo-Json -Depth 8; return }
    if ($null -ne $r -and $r.PSObject.Properties.Name -contains 'ok') {
        if ($r.ok) { Write-Host 'ok' -ForegroundColor Green } else { Write-Host 'failed' -ForegroundColor Red }
        return
    }
    $r | ConvertTo-Json -Depth 8
}

switch ($Command.ToLowerInvariant()) {

    { $_ -in @('help', '-h', '--help', '/?') } { Get-Help $PSCommandPath -Detailed; break }

    'commands' {
        Write-Host @'
pCUE CLI - every command

  Reading
    status                     connection, fans, tach, hold, CPU
    watch [seconds]            live status, refreshed (default 2)
    info                       the API's own endpoint list
    discover                   find pCUE instances on the LAN

  Fans
    duty <fan|all> <0-100>     set fan power %
    rpm  <fan> <rpm>           Commander fixed RPM (4-pin channels only)
    mode <fan> <auto|3pin|4pin|disconnect>
    apply <v1> ... <v6>        apply the six GUI setpoint boxes together
    reset                      clear Min/Max/Avg statistics

  Closed-loop RPM hold
    hold <fan> <rpm>           hold that RPM using tachometer feedback
    stop                       stop the hold
    config                     show the loop tunables
    config <key> <value> ...   set them

    tunables: target tolerance minDuty maxDuty startDuty coarseStep fineStep
              coarseThreshold sampleInterval settleDelay stabilizeTime timeout
              filterWindow maxInvalid dither (0/1 - alternate two adjacent duties
              for a sub-1% average at the resolution limit; on by default)

  Hardware
    open | close               connect / disconnect the Commander PRO
    tach <connect|disconnect>
    assign <fan|none>          which fan the tachometer measures
    cpu <on|off>               CPU monitoring
    average <on|off>           show running average instead of minimum
    autostart <on|off>         launch pCUE with Windows
    autoconnect <on|off>       connect hardware when pCUE starts
    tach-adjust <on|off>       closed-loop tachometer adjustment
    kill-icue                  stop the iCUE services

  Diagnostics
    log [lines]                recent log (default 100)
    loglevel [debug|info|warn|error]
    logclear
    shot [main|help] [file]    PNG of the running UI

  Anywhere: -Server <host> -Token <secret> -Json
  Or set PCUE_SERVER / PCUE_TOKEN once and forget them.

  Examples
    pcue-cli.ps1 duty 2 45
    pcue-cli.ps1 hold 2 1200
    pcue-cli.ps1 config tolerance 15 settleDelay 4500
    pcue-cli.ps1 shot main ui.png
'@
        break
    }

    'discover' {
        $hits = @(Find-Pcue)
        if ($hits.Count -eq 0) { Die 'Nothing answered on UDP 5057.' $EXIT_UNREACHABLE }
        if ($Json) { $hits | ConvertTo-Json -Depth 5; break }
        foreach ($h in $hits) {
            $tok = ''
            if ($h.requiresToken) { $tok = '  (token required)' }
            Write-Host ("  {0,-18} {1,-30} v{2}{3}" -f $h.host, $h.url, $h.version, $tok)
        }
        break
    }

    'status' { Show-Status (Invoke-Api '/status'); break }

    'info' { Invoke-Api '/' | ConvertTo-Json -Depth 6; break }

    'watch' {
        $every = 2
        if ($Rest -and $Rest.Count -ge 1) { [void][int]::TryParse($Rest[0], [ref]$every) }
        if ($every -lt 1) { $every = 1 }
        Write-Host 'Ctrl+C to stop.' -ForegroundColor DarkGray
        while ($true) {
            $s = Invoke-Api '/status'
            if ($Json) { $s | ConvertTo-Json -Depth 8 -Compress }
            else { Clear-Host; Show-Status $s }
            Start-Sleep -Seconds $every
        }
        break
    }

    'duty' {
        Need 2 'duty <fan|all> <0-100>'
        $fan = Get-FanArg $Rest[0] -AllowAll
        $val = 0
        if (-not [int]::TryParse($Rest[1], [ref]$val) -or $val -lt 0 -or $val -gt 100) {
            Die "Duty must be 0-100 (got '$($Rest[1])')." $EXIT_USAGE
        }
        $targets = @($fan)
        if ($fan -eq 'all') { $targets = 1..6 }
        foreach ($f in $targets) { Out-Result (Invoke-Api "/fan/duty?fan=$f&value=$val" 'Post') }
        break
    }

    'rpm' {
        Need 2 'rpm <fan> <rpm>'
        $fan = Get-FanArg $Rest[0]
        Out-Result (Invoke-Api "/fan/rpm?fan=$fan&value=$([int]$Rest[1])" 'Post')
        break
    }

    'mode' {
        Need 2 'mode <fan> <auto|3pin|4pin|disconnect>'
        $fan = Get-FanArg $Rest[0]
        $m = $Rest[1].ToLowerInvariant()
        if ($m -notin @('auto', '3pin', '4pin', 'disconnect')) {
            Die "Mode must be auto, 3pin, 4pin or disconnect (got '$m')." $EXIT_USAGE
        }
        Out-Result (Invoke-Api "/fan/mode?fan=$fan&value=$m" 'Post')
        break
    }

    'apply' {
        Need 6 'apply <fan1> <fan2> <fan3> <fan4> <fan5> <fan6>'
        if ($Rest.Count -ne 6) { Die 'apply requires exactly six setpoints.' $EXIT_USAGE }
        $values = @()
        foreach ($value in $Rest) {
            $parsed = 0
            if (-not [int]::TryParse($value, [ref]$parsed) -or $parsed -lt 0 -or $parsed -gt 3500) {
                Die "Every setpoint must be 0-3500 (got '$value')." $EXIT_USAGE
            }
            $values += $parsed
        }
        Out-Result (Invoke-Api '/fans/apply' 'Post' @{ values = $values })
        break
    }

    'hold' {
        Need 2 'hold <fan> <rpm>'
        $fan = Get-FanArg $Rest[0]
        Out-Result (Invoke-Api "/hold/start?fan=$fan&rpm=$([int]$Rest[1])" 'Post')
        break
    }

    'stop' { Out-Result (Invoke-Api '/hold/stop?x=1' 'Post'); break }

    'config' {
        if (-not $Rest -or $Rest.Count -eq 0) {
            $c = Invoke-Api '/hold/config'
            if ($Json) { $c | ConvertTo-Json -Depth 6 }
            else { $c.PSObject.Properties | ForEach-Object { Write-Host ("  {0,-22} {1}" -f $_.Name, $_.Value) } }
            break
        }
        if ($Rest.Count % 2 -ne 0) { Die 'config takes key/value pairs, e.g. config tolerance 15.' $EXIT_USAGE }
        $pairs = @()
        for ($i = 0; $i -lt $Rest.Count; $i += 2) { $pairs += "$($Rest[$i])=$($Rest[$i + 1])" }
        Out-Result (Invoke-Api ('/hold/config?' + ($pairs -join '&')) 'Post')
        break
    }

    'open' { Out-Result (Invoke-Api '/commander/open?x=1' 'Post'); break }

    'close' { Out-Result (Invoke-Api '/commander/close?x=1' 'Post'); break }

    'cpu' {
        Need 1 'cpu <on|off>'
        $a = $Rest[0].ToLowerInvariant()
        if ($a -notin @('on', 'off', 'start', 'stop')) { Die 'cpu takes on or off.' $EXIT_USAGE }
        $verb = 'stop'
        if ($a -in @('on', 'start')) { $verb = 'start' }
        Out-Result (Invoke-Api ('/cpu/' + $verb + '?x=1') 'Post')
        break
    }

    'tach' {
        Need 1 'tach <connect|disconnect>'
        $a = $Rest[0].ToLowerInvariant()
        if ($a -notin @('connect', 'disconnect')) { Die 'tach takes connect or disconnect.' $EXIT_USAGE }
        Out-Result (Invoke-Api ('/tach/' + $a + '?x=1') 'Post')
        break
    }

    'assign' {
        Need 1 'assign <fan|none>'
        $fan = Get-FanArg $Rest[0] -AllowNone
        Out-Result (Invoke-Api "/tach/assign?fan=$fan" 'Post')
        break
    }

    'reset' { Out-Result (Invoke-Api '/reset?x=1' 'Post'); break }

    { $_ -in @('average', 'autostart', 'autoconnect', 'tach-adjust') } {
        Need 1 "$Command <on|off>"
        $value = $Rest[0].ToLowerInvariant()
        if ($value -notin @('on', 'off')) { Die "$Command takes on or off." $EXIT_USAGE }
        $path = @{
            average = 'average'
            autostart = 'auto-start'
            autoconnect = 'auto-connect'
            'tach-adjust' = 'tacho-adjust'
        }[$Command.ToLowerInvariant()]
        $number = 0
        if ($value -eq 'on') { $number = 1 }
        Out-Result (Invoke-Api "/settings/$path?value=$number" 'Post')
        break
    }

    'kill-icue' { Out-Result (Invoke-Api '/system/kill-icue?x=1' 'Post'); break }

    # Spellings from the previous version of this script. Kept because bench scripts and the
    # handover notes already use them, and silently breaking those to tidy up a command list
    # would cost more than the two lines each.
    'debug' { Out-Result (Invoke-Api '/log/level?value=debug'); break }
    'cpu-start' { Out-Result (Invoke-Api '/cpu/start?x=1' 'Post'); break }
    'cpu-stop' { Out-Result (Invoke-Api '/cpu/stop?x=1' 'Post'); break }
    'tach-connect' { Out-Result (Invoke-Api '/tach/connect?x=1' 'Post'); break }
    'tach-disconnect' { Out-Result (Invoke-Api '/tach/disconnect?x=1' 'Post'); break }

    'log' {
        $n = 100
        if ($Rest -and $Rest.Count -ge 1) { [void][int]::TryParse($Rest[0], [ref]$n) }
        $r = Invoke-Api "/log?tail=$n"
        if ($r -is [string]) { $r }
        elseif ($r.lines) { $r.lines }
        else { $r | ConvertTo-Json -Depth 6 }
        break
    }

    'loglevel' {
        if (-not $Rest -or $Rest.Count -eq 0) { Out-Result (Invoke-Api '/log/level'); break }
        Out-Result (Invoke-Api "/log/level?value=$($Rest[0].ToLowerInvariant())")
        break
    }

    'logclear' { Out-Result (Invoke-Api '/log/clear?x=1' 'Post'); break }

    'shot' {
        $which = 'main'
        $file = $null
        if ($Rest -and $Rest.Count -ge 1) { $which = $Rest[0].ToLowerInvariant() }
        if ($Rest -and $Rest.Count -ge 2) { $file = $Rest[1] }
        if ($which -notin @('main', 'help')) { Die "shot takes main or help (got '$which')." $EXIT_USAGE }
        if (-not $file) { $file = "pcue-$which.png" }

        $base = Resolve-Base
        $headers = @{}
        if ($Token) { $headers['X-pCUE-Token'] = $Token }
        try {
            Invoke-WebRequest -Uri "$base/screenshot?window=$which" -Headers $headers `
                -TimeoutSec $TimeoutSec -OutFile $file | Out-Null
        } catch {
            Die "Screenshot failed - $($_.Exception.Message)" $EXIT_ERROR
        }
        $len = (Get-Item -LiteralPath $file).Length
        # A window that has never been shown renders blank, and a blank PNG is small but perfectly
        # valid - so size is the only cheap signal that the capture actually caught something.
        if ($len -lt 5000) {
            Write-Host "Warning: $file is only $len bytes - it may be blank." -ForegroundColor Yellow
        }
        Write-Host ("{0}  ({1} KB)" -f $file, [math]::Round($len / 1kb, 1))
        break
    }

    default { Die "Unknown command '$Command'. Try: pcue-cli.ps1 commands" $EXIT_USAGE }
}

exit $EXIT_OK
