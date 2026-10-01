<#
  Exercise the real WPF Sync controls without opening the Commander or tachometer.
  Run under Windows PowerShell -STA after the Debug build.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'pCUE\bin\Debug'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Get-ChildItem -LiteralPath $bin -Filter *.dll | ForEach-Object {
    try { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
}

$app = New-Object System.Windows.Application
$app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
$style = New-Object System.Windows.Style ([System.Windows.Controls.Label])
$style.Setters.Add((New-Object System.Windows.Setter ([System.Windows.Controls.Control]::ForegroundProperty, [System.Windows.Media.Brushes]::White)))
$style.Setters.Add((New-Object System.Windows.Setter ([System.Windows.Controls.Control]::FontWeightProperty, [System.Windows.FontWeights]::Bold)))
$app.Resources.Add('White_Labels', $style)

$assembly = [System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'pCUE.exe'))
$window = $assembly.CreateInstance('pCUE.MainWindow')
if (-not $window) { throw 'Could not construct the pCUE window.' }
$numbers = @(1..6 | ForEach-Object { $window.FindName("Fan${_}_Numeric") })
$sliders = @(1..6 | ForEach-Object { $window.FindName("Fan${_}_Slider") })
$sync = $window.FindName('Sync_Fans_CheckBox')
if ($numbers.Where({ $null -eq $_ }).Count -or $sliders.Where({ $null -eq $_ }).Count -or -not $sync) {
    throw 'Fan controls were not found in the WPF window.'
}

function Assert-Controls([uint32]$value) {
    for ($i = 0; $i -lt 6; $i++) {
        if ($numbers[$i].Value -ne $value -or $sliders[$i].Value -ne $value) {
            throw "Fan $($i + 1) shows number $($numbers[$i].Value), slider $($sliders[$i].Value); expected $value."
        }
    }
}

try {
    $numbers[0].Value = 23
    $numbers[1].Value = 31
    if ($sliders[0].Value -ne 23 -or $sliders[1].Value -ne 31) { throw 'Independent controls failed.' }

    $sync.IsChecked = $true
    Assert-Controls 23

    $numbers[0].Value = 35
    Assert-Controls 35

    $numbers[3].Value = 46
    Assert-Controls 46

    $sliders[5].Value = 61
    Assert-Controls 61

    $sync.IsChecked = $false
    $numbers[2].Value = 17
    if ($numbers[2].Value -ne 17 -or $sliders[2].Value -ne 17 -or
        $numbers[0].Value -ne 61 -or $sliders[0].Value -ne 61) {
        throw 'Independent editing changed another fan.'
    }

    Write-Host 'Sync controls: checkbox alignment, numeric, slider, and independent edits passed.'
    # A preview panel opens no hardware/settings. Exercise the actual parent's
    # interlocks so a remote/auto PRO open or updater close cannot bypass it.
    $privateFlags = [System.Reflection.BindingFlags]'Instance,NonPublic'
    $panelType = $assembly.GetType('pCUE.CoolingControllersWindow')
    $panel = [Activator]::CreateInstance($panelType, @($true))
    $panelField = $window.GetType().GetField('coolingControllersWindow', $privateFlags)
    try {
        $panelField.SetValue($window, $panel)
        if ($window.GetType().GetProperty('CanInstallCoolingUpdate', $privateFlags).GetValue($window, $null)) { throw 'Controller panel allowed an update installation.' }
        if (-not $window.SetCommanderOpen($true)) { throw 'Controller panel allowed a PRO open.' }
        $window.GetType().GetField('suppressCloseConfirm', $privateFlags).SetValue($window, $true)
        $closingArgs = New-Object System.ComponentModel.CancelEventArgs
        [void]$window.GetType().GetMethod('Window_Closing', $privateFlags).Invoke($window, @($window, $closingArgs.PSObject.BaseObject))
        if (-not $closingArgs.Cancel) { throw 'Controller panel did not block parent/update closure.' }
        Write-Host 'Cooling panel interlocks: real PRO open refused and parent/update close blocked (no hardware).'
    }
    finally { $panelField.SetValue($window, $null); $panel.Close() }
}
finally {
    $field = $window.GetType().GetField('suppressCloseConfirm',
        [System.Reflection.BindingFlags]'Instance,NonPublic')
    $field.SetValue($window, $true)
    $window.Close()
    $app.Shutdown()
}
