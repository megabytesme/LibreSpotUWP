[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Smoke', 'AutoNext')]
    [string]$Mode = 'Smoke',
    [string]$PackageFamilyName = '34151aa6-2b17-4a37-bddb-2ee83fdf53e4_t66b0z1ra86tw',
    [string]$ApplicationId = 'App',
    [int]$StartupTimeoutSeconds = 60,
    [int]$PlaybackTimeoutSeconds = 20,
    [switch]$ClearAppData
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LibreSpotUiNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
}
'@

$script:Results = [ordered]@{
    mode = $Mode
    clearAppData = [bool]$ClearAppData
    startedAt = (Get-Date).ToString('o')
    checks = [System.Collections.Generic.List[object]]::new()
}

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail, [double]$ElapsedMs = 0)
    $script:Results.checks.Add([ordered]@{
        name = $Name
        passed = $Passed
        elapsedMs = [math]::Round($ElapsedMs, 1)
        detail = $Detail
    })
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Host "[$status] $Name ($([math]::Round($ElapsedMs, 1)) ms): $Detail"
    if (-not $Passed) { throw "$Name failed: $Detail" }
}

function Clear-PackageData {
    $packageRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Packages\$PackageFamilyName"))
    $packagesRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Packages'))
    if (-not $packageRoot.StartsWith($packagesRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a path outside $packagesRoot"
    }
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "Package data directory does not exist: $packageRoot"
    }

    Get-Process -Name 'LibreSpotUWP' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    foreach ($name in @('LocalState', 'LocalCache', 'TempState')) {
        $target = [IO.Path]::GetFullPath((Join-Path $packageRoot $name))
        if (-not $target.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear unexpected target: $target"
        }
        if (Test-Path -LiteralPath $target -PathType Container) {
            Get-ChildItem -LiteralPath $target -Force | Remove-Item -Recurse -Force
        }
    }
    Write-Host "Cleared LocalState, LocalCache, and TempState for $PackageFamilyName."
}

function Find-Window {
    $condition = [Windows.Automation.AndCondition]::new(@(
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty,
            'LibreSpotUWP'),
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ClassNameProperty,
            'ApplicationFrameWindow')
    ))
    [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSeconds, [string]$Description)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Condition
        if ($null -ne $value -and $value -ne $false) {
            return [pscustomobject]@{ Value = $value; ElapsedMs = $timer.Elapsed.TotalMilliseconds }
        }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Timed out waiting for $Description after $TimeoutSeconds seconds."
}

function Find-ById {
    param($Root, [string]$AutomationId)
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ByName {
    param($Root, [string]$Name)
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Element {
    param($Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
        return
    }
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
        return
    }
    Click-Element $Element
}

function Click-Element {
    param($Element, [double]$FractionX = 0.5, [double]$FractionY = 0.5)
    if ($null -ne $script:AppRoot) {
        $script:AppRoot.SetFocus()
        [LibreSpotUiNative]::SetForegroundWindow(
            [IntPtr]$script:AppRoot.Current.NativeWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 200
    }
    $rect = $Element.Current.BoundingRectangle
    if ($rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0) {
        throw "Element '$($Element.Current.Name)' has no clickable bounds."
    }
    $x = [int]($rect.Left + ($rect.Width * $FractionX))
    $y = [int]($rect.Top + ($rect.Height * $FractionY))
    [LibreSpotUiNative]::SetCursorPos($x, $y) | Out-Null
    [LibreSpotUiNative]::mouse_event([LibreSpotUiNative]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [LibreSpotUiNative]::mouse_event([LibreSpotUiNative]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-Text {
    param($Element)
    if ($null -eq $Element) { return '' }
    $Element.Current.Name
}

function Convert-TimeTextToSeconds {
    param([string]$Value)
    if ($Value -match '^(\d+):(\d{2})$') {
        return ([int]$Matches[1] * 60) + [int]$Matches[2]
    }
    return -1
}

function Get-TrackRow {
    param($List)
    $items = $List.FindAll(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($item in $items) {
        $rect = $item.Current.BoundingRectangle
        $isTrackItem = $item.Current.ControlType -eq [Windows.Automation.ControlType]::ListItem -or
            $item.Current.ControlType -eq [Windows.Automation.ControlType]::DataItem
        if ($isTrackItem -and -not $rect.IsEmpty -and $rect.Height -ge 30 -and
            $rect.Width -ge 200 -and $item.Current.IsEnabled) {
            return $item
        }
    }
    return $null
}

function Wait-ForClockAdvance {
    param($Clock, [int]$InitialSeconds, [int]$TimeoutSeconds)
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Description 'the playback clock to advance' -Condition {
        $current = Convert-TimeTextToSeconds (Get-Text $Clock)
        if ($current -gt $InitialSeconds) { return $current }
        return $null
    }
}

try {
    if ($ClearAppData) { Clear-PackageData }
    else { Get-Process -Name 'LibreSpotUWP' -ErrorAction SilentlyContinue | Stop-Process -Force }

    $launchTimer = [Diagnostics.Stopwatch]::StartNew()
    Start-Process 'explorer.exe' "shell:AppsFolder\$PackageFamilyName!$ApplicationId"
    $windowWait = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description 'LibreSpotUWP window' -Condition { Find-Window }
    $root = $windowWait.Value
    $script:AppRoot = $root
    $root.SetFocus()
    [LibreSpotUiNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle) | Out-Null
    Add-Check 'window-ready' $true 'LibreSpotUWP top-level window found.' $launchTimer.Elapsed.TotalMilliseconds

    if ($Mode -eq 'Inspect') {
        $elements = $root.FindAll(
            [Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.Condition]::TrueCondition)
        $script:Results.elements = @($elements | ForEach-Object {
            [ordered]@{
                id = $_.Current.AutomationId
                name = $_.Current.Name
                type = $_.Current.ControlType.ProgrammaticName
                enabled = $_.Current.IsEnabled
            }
        })
        $script:Results.finishedAt = (Get-Date).ToString('o')
        $script:Results | ConvertTo-Json -Depth 8
        exit 0
    }

    $likedWait = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description 'Liked Songs navigation item' -Condition { Find-ByName $root 'Liked Songs' }
    Add-Check 'signed-in-shell-ready' $true 'Signed-in navigation loaded.' $likedWait.ElapsedMs
    Invoke-Element $likedWait.Value

    $listWait = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description 'Liked Songs track list' -Condition {
        $list = Find-ById $root 'TrackListView'
        if ($null -eq $list) { return $null }
        $row = Get-TrackRow $list
        if ($null -eq $row) { return $null }
        return [pscustomobject]@{ List = $list; Row = $row }
    }
    Add-Check 'library-content-ready' $true 'At least one liked-song row loaded.' $listWait.ElapsedMs

    $title = Find-ById $root 'TrackTitle'
    $clock = Find-ById $root 'CurrentTime'
    $playPause = Find-ById $root 'PlayPauseButton'
    $next = Find-ById $root 'NextButton'
    $previous = Find-ById $root 'PrevButton'
    $slider = Find-ById $root 'PositionSlider'
    foreach ($required in @($title, $clock, $playPause, $next, $previous, $slider)) {
        if ($null -eq $required) { throw 'A required playback control was not found.' }
    }

    # The second line contains artist hyperlinks; click the title line so the
    # row's playback Tapped handler receives the input.
    Click-Element $listWait.Value.Row 0.25 0.22
    $playWait = Wait-Until -TimeoutSeconds $PlaybackTimeoutSeconds -Description 'initial app-started playback' -Condition {
        $name = Get-Text $title
        $seconds = Convert-TimeTextToSeconds (Get-Text $clock)
        # The prior Connect session may still be visible while the row click
        # is being processed. A row click starts this deterministic queue at
        # the beginning, so do not accept an already-advanced stale clock.
        if (-not [string]::IsNullOrWhiteSpace($name) -and
            $seconds -ge 1 -and $seconds -le 10) {
            return [pscustomobject]@{ Title = $name; Seconds = $seconds }
        }
        return $null
    }
    $firstTitle = $playWait.Value.Title
    Add-Check 'initial-app-playback' $true "$firstTitle advanced to $($playWait.Value.Seconds)s." $playWait.ElapsedMs

    Invoke-Element $playPause
    Start-Sleep -Seconds 1
    $pausedAt = Convert-TimeTextToSeconds (Get-Text $clock)
    Start-Sleep -Seconds 2
    $pausedLater = Convert-TimeTextToSeconds (Get-Text $clock)
    Add-Check 'pause' ($pausedAt -ge 0 -and [math]::Abs($pausedLater - $pausedAt) -le 1) "Clock held at ${pausedLater}s."

    Invoke-Element $playPause
    $resumeWait = Wait-ForClockAdvance $clock $pausedLater $PlaybackTimeoutSeconds
    Add-Check 'resume' $true "Clock advanced from ${pausedLater}s to $($resumeWait.Value)s." $resumeWait.ElapsedMs

    $positionRange = $null
    if (-not $slider.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern, [ref]$positionRange)) {
        throw 'The playback slider does not expose a range-value pattern.'
    }
    $maximumMs = [double]$positionRange.Current.Maximum
    $currentMs = [double]$positionRange.Current.Value
    if ($maximumMs -lt 45000) {
        throw "The selected track is too short for a deterministic slider seek ($maximumMs ms)."
    }
    $targetMs = [math]::Min($maximumMs - 15000, [math]::Max(15000, $currentMs + 30000))
    $targetSeconds = [int][math]::Floor($targetMs / 1000)
    $seekFraction = ($targetMs - [double]$positionRange.Current.Minimum) /
        ($maximumMs - [double]$positionRange.Current.Minimum)
    Click-Element $slider $seekFraction 0.5
    $seekWait = Wait-Until -TimeoutSeconds $PlaybackTimeoutSeconds -Description 'playback-slider seek' -Condition {
        $seconds = Convert-TimeTextToSeconds (Get-Text $clock)
        if ([math]::Abs($seconds - $targetSeconds) -le 4) { return $seconds }
        return $null
    }
    $seekAdvance = Wait-ForClockAdvance $clock $seekWait.Value $PlaybackTimeoutSeconds
    Add-Check 'playback-slider-seek' $true "Pointer click sought to $($seekWait.Value)s and advanced to $($seekAdvance.Value)s." $seekWait.ElapsedMs

    $beforeNext = Get-Text $title
    Invoke-Element $next
    $nextWait = Wait-Until -TimeoutSeconds $PlaybackTimeoutSeconds -Description 'manual next-track playback' -Condition {
        $name = Get-Text $title
        $seconds = Convert-TimeTextToSeconds (Get-Text $clock)
        if ($name -ne $beforeNext -and $seconds -ge 1) { return [pscustomobject]@{ Title = $name; Seconds = $seconds } }
        return $null
    }
    Add-Check 'manual-next' $true "$($nextWait.Value.Title) is playing." $nextWait.ElapsedMs

    $beforePrevious = Get-Text $title
    Invoke-Element $previous
    $previousWait = Wait-Until -TimeoutSeconds $PlaybackTimeoutSeconds -Description 'manual previous-track playback' -Condition {
        $name = Get-Text $title
        $seconds = Convert-TimeTextToSeconds (Get-Text $clock)
        if ($name -ne $beforePrevious -and $seconds -ge 1) { return [pscustomobject]@{ Title = $name; Seconds = $seconds } }
        return $null
    }
    Add-Check 'manual-previous' $true "$($previousWait.Value.Title) is playing." $previousWait.ElapsedMs

    if ($Mode -eq 'AutoNext') {
        $beforeAuto = Get-Text $title
        $range = $null
        $remainingSeconds = 300
        if ($slider.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern, [ref]$range) -and
            $range.Current.Maximum -gt $range.Current.Value) {
            $remainingSeconds = [math]::Ceiling(($range.Current.Maximum - $range.Current.Value) / 1000)
        }
        $automaticTimeout = [int]($remainingSeconds + $PlaybackTimeoutSeconds + 20)
        Write-Host "Waiting up to $automaticTimeout seconds for the real end of '$beforeAuto'."
        $autoWait = Wait-Until -TimeoutSeconds $automaticTimeout -Description 'automatic next-track playback' -Condition {
            $name = Get-Text $title
            $seconds = Convert-TimeTextToSeconds (Get-Text $clock)
            if ($name -ne $beforeAuto -and $seconds -ge 1) { return [pscustomobject]@{ Title = $name; Seconds = $seconds } }
            return $null
        }
        Add-Check 'automatic-next' $true "$($autoWait.Value.Title) automatically continued and advanced." $autoWait.ElapsedMs
    }
}
catch {
    $script:Results.error = $_.Exception.Message
    Write-Error $_
    $script:Results.finishedAt = (Get-Date).ToString('o')
    $script:Results | ConvertTo-Json -Depth 8
    exit 1
}
finally {
    if (-not $script:Results.finishedAt) { $script:Results.finishedAt = (Get-Date).ToString('o') }
}

$script:Results | ConvertTo-Json -Depth 8
