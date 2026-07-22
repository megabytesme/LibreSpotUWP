[CmdletBinding()]
param(
    [string]$LibreSpotDeviceName = 'BEN-DESKTOP',
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SpotifyConnectUiNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
}
'@

$script:Checks = [System.Collections.Generic.List[object]]::new()

function Add-Check {
    param([string]$Name, [string]$Detail)
    $script:Checks.Add([ordered]@{ name = $Name; passed = $true; detail = $Detail })
    Write-Host "[PASS] ${Name}: $Detail"
}

function Wait-Until {
    param([scriptblock]$Condition, [string]$Description, [int]$Seconds = $TimeoutSeconds)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Condition
        if ($null -ne $value -and $value -ne $false) { return $value }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw "Timed out waiting for $Description after $Seconds seconds."
}

function Get-SpotifyRoot {
    $process = Get-Process Spotify -ErrorAction SilentlyContinue |
        Where-Object MainWindowHandle -ne 0 |
        Select-Object -First 1
    if ($null -eq $process) { return $null }
    [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$process.MainWindowHandle)
}

function Get-LibreRoot {
    [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty,
            'LibreSpotUWP'))
}

function Find-Element {
    param($Root, [string]$Name, $ControlType = $null)
    $conditions = [System.Collections.Generic.List[Windows.Automation.Condition]]::new()
    $conditions.Add([Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty,
        $Name))
    if ($null -ne $ControlType) {
        $conditions.Add([Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType))
    }
    $condition = if ($conditions.Count -eq 1) {
        $conditions[0]
    } else {
        [Windows.Automation.AndCondition]::new($conditions.ToArray())
    }
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-VisibleElement {
    param($Root, [string]$Name, $ControlType = $null)
    $conditions = [System.Collections.Generic.List[Windows.Automation.Condition]]::new()
    $conditions.Add([Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty,
        $Name))
    if ($null -ne $ControlType) {
        $conditions.Add([Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType))
    }
    $condition = if ($conditions.Count -eq 1) {
        $conditions[0]
    } else {
        [Windows.Automation.AndCondition]::new($conditions.ToArray())
    }
    $matches = $Root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($match in $matches) {
        $rect = $match.Current.BoundingRectangle
        if (-not $match.Current.IsOffscreen -and -not $rect.IsEmpty -and
            $rect.Width -gt 0 -and $rect.Height -gt 0) {
            return $match
        }
    }
    return $null
}

function Find-VisibleSpotifyDevice {
    param($Root, [string]$Name)
    $matches = $Root.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($controlType in @(
        [Windows.Automation.ControlType]::Button,
        [Windows.Automation.ControlType]::Group)) {
        foreach ($match in $matches) {
            $candidateName = $match.Current.Name
            $rect = $match.Current.BoundingRectangle
            if ($match.Current.ControlType -eq $controlType -and
                ($candidateName -eq $Name -or $candidateName.StartsWith("$Name ")) -and
                -not $match.Current.IsOffscreen -and -not $rect.IsEmpty -and
                $rect.Width -gt 0 -and $rect.Height -gt 0) {
                return $match
            }
        }
    }
    return $null
}

function Click-Element {
    param($Root, $Element, [double]$FractionX = 0.5)
    if ($null -eq $Element) { throw 'Cannot click a missing UI element.' }
    $rect = $Element.Current.BoundingRectangle
    if ($rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0) {
        throw "Element '$($Element.Current.Name)' has no clickable bounds."
    }
    [SpotifyConnectUiNative]::SetForegroundWindow([IntPtr]$Root.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 200
    [SpotifyConnectUiNative]::SetCursorPos(
        [int]($rect.Left + ($rect.Width * $FractionX)),
        [int]($rect.Top + ($rect.Height / 2))) | Out-Null
    [SpotifyConnectUiNative]::mouse_event([SpotifyConnectUiNative]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [SpotifyConnectUiNative]::mouse_event([SpotifyConnectUiNative]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

function Invoke-OrClickElement {
    param($Root, $Element)
    $invoke = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
        $invoke.Invoke()
        return
    }
    Click-Element $Root $Element
}

function Select-SpotifyDevice {
    param([string]$Name)
    $root = Get-SpotifyRoot
    $device = Find-VisibleSpotifyDevice $root $Name
    $script:NextPanelOpenAttempt = [DateTime]::MinValue
    $device = Wait-Until -Description "Spotify device '$Name'" -Condition {
        $currentRoot = Get-SpotifyRoot
        $visibleDevice = Find-VisibleSpotifyDevice $currentRoot $Name
        if ($null -ne $visibleDevice) { return $visibleDevice }

        if ([DateTime]::UtcNow -ge $script:NextPanelOpenAttempt) {
            $connect = Find-Element $currentRoot 'Connect to a device' ([Windows.Automation.ControlType]::Button)
            if ($null -ne $connect) {
                Invoke-OrClickElement $currentRoot $connect
            }
            $script:NextPanelOpenAttempt = [DateTime]::UtcNow.AddSeconds(2)
        }
        return $null
    }
    Invoke-OrClickElement (Get-SpotifyRoot) $device
}

function Get-LibreText {
    param([string]$AutomationId)
    $root = Get-LibreRoot
    if ($null -eq $root) { return $null }
    $element = $root.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId))
    if ($null -eq $element) { return $null }
    $element.Current.Name
}

function Convert-TimeTextToSeconds {
    param([string]$Value)
    if ($Value -match '^(\d+):(\d{2})$') {
        return ([int]$Matches[1] * 60) + [int]$Matches[2]
    }
    -1
}

function Test-SpotifyPlaying {
    $root = Get-SpotifyRoot
    $null -ne $root -and
        $null -ne (Find-Element $root 'Pause' ([Windows.Automation.ControlType]::Button))
}

function Test-SpotifyRemote {
    $root = Get-SpotifyRoot
    $null -ne $root -and
        $null -ne (Find-Element $root "Playing on $LibreSpotDeviceName" ([Windows.Automation.ControlType]::Button))
}

try {
    Wait-Until -Description 'both application windows' -Condition {
        if ($null -ne (Get-SpotifyRoot) -and $null -ne (Get-LibreRoot)) { return $true }
        return $null
    } | Out-Null

    Wait-Until -Description 'Spotify observing active LibreSpot playback' -Condition {
        if ((Test-SpotifyPlaying) -and (Test-SpotifyRemote)) { return $true }
        return $null
    } | Out-Null
    Add-Check 'initial-connect-state' "Spotify shows active playback on $LibreSpotDeviceName."

    Select-SpotifyDevice 'This computer'
    Wait-Until -Description 'transfer to the official Spotify client' -Condition {
        if ((Test-SpotifyPlaying) -and -not (Test-SpotifyRemote)) { return $true }
        return $null
    } | Out-Null
    Start-Sleep -Seconds 3
    if (Test-SpotifyRemote) { throw 'LibreSpot reclaimed playback after transfer to This computer.' }
    Add-Check 'transfer-away-stable' 'Official Spotify remained the active device.'

    Select-SpotifyDevice $LibreSpotDeviceName
    Wait-Until -Description 'transfer back to LibreSpotUWP' -Condition {
        $title = Get-LibreText 'TrackTitle'
        $seconds = Convert-TimeTextToSeconds (Get-LibreText 'CurrentTime')
        if ((Test-SpotifyPlaying) -and (Test-SpotifyRemote) -and
            -not [string]::IsNullOrWhiteSpace($title) -and $title -ne 'Unknown Track' -and
            $seconds -ge 1) {
            return [pscustomobject]@{ Title = $title; Seconds = $seconds }
        }
        return $null
    } | ForEach-Object { $transferResult = $_ }
    $beforeAdvance = $transferResult.Seconds
    $afterAdvance = Wait-Until -Description 'LibreSpot clock after transfer' -Condition {
        $seconds = Convert-TimeTextToSeconds (Get-LibreText 'CurrentTime')
        if ($seconds -gt $beforeAdvance) { return $seconds }
        return $null
    }
    Add-Check 'transfer-back-playing' "$($transferResult.Title) advanced from ${beforeAdvance}s to ${afterAdvance}s."

    $stableAdvance = Wait-Until -Description 'stable Connect playback after transfer' -Seconds 15 -Condition {
        $seconds = Convert-TimeTextToSeconds (Get-LibreText 'CurrentTime')
        if ((Test-SpotifyPlaying) -and (Test-SpotifyRemote) -and
            $seconds -ge ($afterAdvance + 5)) {
            return $seconds
        }
        return $null
    }
    Add-Check 'transfer-back-stable' "Connect ownership and playback remained active through ${stableAdvance}s."

    $spotifyRoot = Get-SpotifyRoot
    $progress = Find-Element $spotifyRoot 'Change progress' ([Windows.Automation.ControlType]::Slider)
    $range = $null
    if ($null -eq $progress -or
        -not $progress.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern, [ref]$range)) {
        throw 'Spotify progress slider was not available.'
    }
    $fraction = if ($range.Current.Value -lt ($range.Current.Maximum * 0.55)) { 0.70 } else { 0.25 }
    $targetSeconds = [int][math]::Floor(($range.Current.Maximum * $fraction) / 1000)
    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    $clickTrack = $walker.GetParent($walker.GetParent($progress))
    Click-Element $spotifyRoot $clickTrack $fraction

    $seekResult = Wait-Until -Description 'remote Spotify seek in LibreSpotUWP' -Condition {
        $seconds = Convert-TimeTextToSeconds (Get-LibreText 'CurrentTime')
        if ([math]::Abs($seconds - $targetSeconds) -le 6) { return $seconds }
        return $null
    }
    $seekAdvance = Wait-Until -Description 'playback after remote Spotify seek' -Condition {
        $seconds = Convert-TimeTextToSeconds (Get-LibreText 'CurrentTime')
        if ($seconds -gt $seekResult) { return $seconds }
        return $null
    }
    if (-not (Test-SpotifyPlaying) -or -not (Test-SpotifyRemote)) {
        throw 'Playback or Connect ownership was lost after the remote seek.'
    }
    Add-Check 'remote-seek-playing' "Spotify sought to ${seekResult}s and LibreSpot advanced to ${seekAdvance}s."

    [ordered]@{
        startedWithDevice = $LibreSpotDeviceName
        checks = $script:Checks
        finishedAt = (Get-Date).ToString('o')
    } | ConvertTo-Json -Depth 6
}
catch {
    Write-Error $_
    exit 1
}
