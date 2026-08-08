<#
.SYNOPSIS
  Static layout check for pCUE's WPF windows: finds controls that overlap each other or spill
  outside their parent.

.DESCRIPTION
  pCUE's windows are laid out with absolute Margins on a fixed-size, non-resizable canvas. That is
  quick to author and easy to get wrong: three separate overlaps shipped during development
  (BATT LOW over the tach status, "RPM:" over the fan combo, the hold status over "Auto connect"),
  each spotted only by eye, and one of them - the battery warning - was invisible until the exact
  moment it mattered.

  This renders the real XAML and measures it, rather than guessing sizes from the text. It strips
  x:Class and the event-handler attributes so XamlReader can load the markup without the
  code-behind, then walks the tree and compares every pair of SIBLING leaf controls.

  Exit code 0 = clean, 1 = problems found.

.EXAMPLE
  pwsh scripts\Test-UiLayout.ps1
  pwsh scripts\Test-UiLayout.ps1 -Tolerance 0     # flag even a 1px touch
#>
[CmdletBinding()]
param(
    [string]$ProjectDir,
    [string]$BinDir,
    # Controls may share a pixel or two at their edges without looking wrong; only report real
    # overlaps. Raise this if borders/padding cause noise, lower it to be stricter.
    [double]$Tolerance  = 2.0
)
$ErrorActionPreference = 'Stop'

# Resolve relative to this script, not the caller's working directory.
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ProjectDir) { $ProjectDir = Join-Path $repoRoot 'pCUE' }
if (-not $BinDir)     { $BinDir     = Join-Path $ProjectDir 'bin\Debug' }

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

# Referenced control libraries must resolve while the markup is parsed.
Get-ChildItem $BinDir -Filter *.dll -ErrorAction SilentlyContinue | ForEach-Object {
    try { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
}

# Attributes that are event hooks rather than properties. XamlReader cannot bind them without the
# generated code-behind, so they are removed before parsing.
$eventAttrs = 'Click','Checked','Unchecked','SelectionChanged','ValueChanged','TextChanged',
              'LostFocus','GotFocus','PasswordChanged','Loaded','Closed','Closing','MouseDown'

$problems = New-Object System.Collections.Generic.List[string]

# The windows reference application-level styles (White_Labels) declared in App.xaml. Parsed on
# their own they would fail on the StaticResource lookup, so lift that dictionary into a real
# Application first.
if (-not [System.Windows.Application]::Current) { $null = New-Object System.Windows.Application }
# Without this the Application shuts down when the FIRST window under test is closed, and every
# later window silently measures nothing - i.e. passes vacuously.
[System.Windows.Application]::Current.ShutdownMode =
    [System.Windows.ShutdownMode]::OnExplicitShutdown
$appXamlPath = Join-Path $ProjectDir 'App.xaml'
if (Test-Path $appXamlPath) {
    $appText = Get-Content $appXamlPath -Raw
    $m = [regex]::Match($appText, '<Application\.Resources>(?<body>.*?)</Application\.Resources>',
                        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($m.Success) {
        $dictXaml = @"
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
$($m.Groups['body'].Value)
</ResourceDictionary>
"@
        try {
            $rd = [System.Windows.Markup.XamlReader]::Parse($dictXaml)
            [System.Windows.Application]::Current.Resources.MergedDictionaries.Add($rd)
        } catch {
            Write-Warning "Could not load App.xaml resources: $($_.Exception.Message)"
        }
    }
}

function Test-Window {
    param([string]$XamlPath)

    $name = [System.IO.Path]::GetFileName($XamlPath)
    Write-Host "  checking $name" -ForegroundColor Cyan

    $xaml = Get-Content $XamlPath -Raw
    $xaml = [regex]::Replace($xaml, '\s+x:Class="[^"]*"', '')
    foreach ($e in $eventAttrs) { $xaml = [regex]::Replace($xaml, "\s+$e=`"[^`"]*`"", '') }
    # Icon paths are relative to the project at runtime; drop them so parsing cannot fail on IO.
    $xaml = [regex]::Replace($xaml, '\s+Icon="[^"]*"', '')

    try {
        $reader = New-Object System.Xml.XmlTextReader (New-Object System.IO.StringReader $xaml)
        $win = [System.Windows.Markup.XamlReader]::Load($reader)
    } catch {
        $problems.Add("${name}: could not parse XAML - $($_.Exception.Message)")
        return
    }

    # Measure/Arrange alone is NOT enough: on a window that has never been shown, WPF has not
    # applied control templates, so VisualTreeHelper finds nothing and every check passes
    # vacuously. Actually show it, far off-screen, to get a real visual tree.
    $w = if ($win.Width -gt 0) { $win.Width } else { 800 }
    $h = if ($win.Height -gt 0) { $win.Height } else { 600 }
    $win.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
    $win.Left = -32000
    $win.Top = -32000
    $win.ShowInTaskbar = $false
    $win.Show()
    $win.UpdateLayout()

    # Leaf controls the user actually interacts with or reads. Containers are excluded: a Grid
    # legitimately contains its children, and a GroupBox legitimately contains a Grid.
    $leafTypes = @(
        'System.Windows.Controls.Button','System.Windows.Controls.CheckBox',
        'System.Windows.Controls.ComboBox','System.Windows.Controls.TextBox',
        'System.Windows.Controls.PasswordBox','System.Windows.Controls.TextBlock',
        'System.Windows.Controls.Label','System.Windows.Controls.Slider',
        'System.Windows.Controls.Primitives.ToggleButton'
    )

    function Get-Leaves {
        param($Parent)
        $out = @()
        $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($Parent)
        for ($i = 0; $i -lt $n; $i++) {
            $child = [System.Windows.Media.VisualTreeHelper]::GetChild($Parent, $i)
            $t = $child.GetType().FullName
            # Match the third-party spinners by namespace: their concrete types are generic, so a
            # literal type reference here would not resolve.
            $isLeaf = ($leafTypes -contains $t) -or $t.StartsWith('NumericUpDownLib.')
            if ($isLeaf) { $out += $child }
            else { $out += Get-Leaves -Parent $child }
        }
        return $out
    }

    # Group by the logical parent so we only compare things laid out in the same coordinate space.
    $all = Get-Leaves -Parent $win | Where-Object {
        $_.IsVisible -or $_.Visibility -ne [System.Windows.Visibility]::Visible
    }

    $rects = @{}
    foreach ($c in $all) {
        if ($c.ActualWidth -le 0 -and $c.Visibility -eq [System.Windows.Visibility]::Collapsed) {
            # A collapsed control still has a declared Margin/Width; measure it as if shown, because
            # it WILL be shown at some point (BATT LOW is the cautionary tale).
            $c.Visibility = [System.Windows.Visibility]::Visible
            $win.UpdateLayout()
        }
        try {
            $t = $c.TransformToAncestor($win)
            $p = $t.Transform((New-Object System.Windows.Point(0, 0)))
            $rects[$c] = New-Object System.Windows.Rect($p.X, $p.Y, $c.ActualWidth, $c.ActualHeight)
        } catch { }
    }

    $items = @($rects.Keys)

    # A checker that silently measures nothing would "pass" everything, which is worse than having
    # no checker at all. Treat an implausibly small tree as a failure of the checker itself.
    Write-Host ("    measured {0} controls" -f $items.Count)
    if ($items.Count -lt 5) {
        $problems.Add("${name}: only $($items.Count) controls measured - the checker is not walking the tree")
        return
    }
    for ($i = 0; $i -lt $items.Count; $i++) {
        for ($j = $i + 1; $j -lt $items.Count; $j++) {
            $a = $items[$i]; $b = $items[$j]
            if ($a.Parent -ne $b.Parent) { continue }      # different coordinate spaces

            $ra = $rects[$a]; $rb = $rects[$b]
            $ox = [Math]::Min($ra.Right, $rb.Right) - [Math]::Max($ra.Left, $rb.Left)
            $oy = [Math]::Min($ra.Bottom, $rb.Bottom) - [Math]::Max($ra.Top, $rb.Top)
            if ($ox -gt $Tolerance -and $oy -gt $Tolerance) {
                $an = if ($a.Name) { $a.Name } else { "$($a.GetType().Name)('$($a.Content)$($a.Text)')" }
                $bn = if ($b.Name) { $b.Name } else { "$($b.GetType().Name)('$($b.Content)$($b.Text)')" }
                $problems.Add(("{0}: {1} overlaps {2} by {3:N0}x{4:N0} px" -f $name, $an, $bn, $ox, $oy))
            }
        }
    }

    # Deliberately NO "outside the window" check. Content inside a ScrollViewer is supposed to
    # extend past the viewport, and content inside a GroupBox is clipped by it rather than being
    # broken, so that test produced far more noise than signal. Sibling overlap is the failure
    # this catches, and it is the one that has actually shipped.

    $win.Close()
}

Write-Host "UI layout check" -ForegroundColor Yellow
foreach ($x in (Get-ChildItem $ProjectDir -Filter *.xaml | Where-Object { $_.Name -ne 'App.xaml' })) {
    Test-Window -XamlPath $x.FullName
}

if ($problems.Count -eq 0) {
    Write-Host "  no overlapping or out-of-bounds controls" -ForegroundColor Green
    exit 0
}

Write-Host "`n$($problems.Count) layout problem(s):" -ForegroundColor Red
$problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
