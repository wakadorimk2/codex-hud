[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$HookCommandWindows,

    [string]$HookCommand,

    [string]$RemoveHookCommandWindows,

    [string]$CodexHomePath,

    [string[]]$Events = @(
        'SessionStart',
        'UserPromptSubmit',
        'PermissionRequest',
        'Stop',
        'SessionEnd'
    ),

    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($HookCommandWindows)) {
    throw 'HookCommandWindows must not be empty.'
}

if ([string]::IsNullOrWhiteSpace($HookCommand)) {
    $HookCommand = $HookCommandWindows
}

if (-not [string]::IsNullOrWhiteSpace($RemoveHookCommandWindows) -and
    $RemoveHookCommandWindows -eq $HookCommandWindows) {
    throw 'RemoveHookCommandWindows must differ from HookCommandWindows.'
}

if ([string]::IsNullOrWhiteSpace($CodexHomePath)) {
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $CodexHomePath = $env:CODEX_HOME
    }
    else {
        $CodexHomePath = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
    }
}

$configPath = Join-Path $CodexHomePath 'hooks.json'

if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    $configText = Get-Content -LiteralPath $configPath -Raw -Encoding utf8
    $config = $configText | ConvertFrom-Json
    if ($null -eq $config) {
        $config = [pscustomobject]@{}
    }
}
else {
    $config = [pscustomobject]@{}
}

if ($null -eq $config.PSObject.Properties['hooks']) {
    Add-Member -InputObject $config -MemberType NoteProperty -Name 'hooks' -Value ([pscustomobject]@{})
}

if ($null -eq $config.hooks) {
    $config.hooks = [pscustomobject]@{}
}

function Repair-HookCommand {
    param(
        [Parameter(Mandatory = $true)]
        [object]$EventValue
    )

    $matched = $false
    $repaired = $false

    foreach ($group in @($EventValue)) {
        if ($null -eq $group -or $null -eq $group.PSObject.Properties['hooks']) {
            continue
        }

        foreach ($handler in @($group.hooks)) {
            if ($null -eq $handler) {
                continue
            }

            foreach ($propertyName in @('commandWindows', 'command')) {
                $property = $handler.PSObject.Properties[$propertyName]
                if ($null -ne $property -and $property.Value -eq $HookCommandWindows) {
                    $matched = $true
                    $asyncProperty = $handler.PSObject.Properties['async']
                    if ($null -ne $asyncProperty) {
                        $handler.PSObject.Properties.Remove('async')
                        $repaired = $true
                    }
                    break
                }
            }
        }
    }

    return [pscustomobject]@{
        Matched = $matched
        Repaired = $repaired
    }
}

function Remove-HookCommand {
    param(
        [Parameter(Mandatory = $true)]
        [object]$EventValue,

        [Parameter(Mandatory = $true)]
        [string]$CommandWindows
    )

    $removed = $false
    $groups = [System.Collections.Generic.List[object]]::new()

    foreach ($group in @($EventValue)) {
        if ($null -eq $group -or $null -eq $group.PSObject.Properties['hooks']) {
            $groups.Add($group)
            continue
        }

        $keptHandlers = [System.Collections.Generic.List[object]]::new()
        foreach ($handler in @($group.hooks)) {
            if ($null -eq $handler) {
                continue
            }

            $matches = $false
            foreach ($propertyName in @('commandWindows', 'command')) {
                $property = $handler.PSObject.Properties[$propertyName]
                if ($null -ne $property -and $property.Value -eq $CommandWindows) {
                    $matches = $true
                    break
                }
            }

            if ($matches) {
                $removed = $true
            }
            else {
                $keptHandlers.Add($handler)
            }
        }

        if ($keptHandlers.Count -gt 0) {
            $group.hooks = $keptHandlers.ToArray()
            $groups.Add($group)
        }
    }

    return [pscustomobject]@{
        Removed = $removed
        Groups = $groups.ToArray()
    }
}

$addedEvents = [System.Collections.Generic.List[string]]::new()
$existingEvents = [System.Collections.Generic.List[string]]::new()
$repairedEvents = [System.Collections.Generic.List[string]]::new()
$removedEvents = [System.Collections.Generic.List[string]]::new()

foreach ($eventName in $Events) {
    if ($eventName -notmatch '^[A-Za-z][A-Za-z0-9]*$') {
        throw "Invalid event name: $eventName"
    }

    $eventProperty = $config.hooks.PSObject.Properties[$eventName]
    if ($null -ne $eventProperty -and -not [string]::IsNullOrWhiteSpace($RemoveHookCommandWindows)) {
        $removeResult = Remove-HookCommand -EventValue $eventProperty.Value -CommandWindows $RemoveHookCommandWindows
        if ($removeResult.Removed) {
            $eventProperty.Value = $removeResult.Groups
            $removedEvents.Add($eventName)
        }
    }

    $eventProperty = $config.hooks.PSObject.Properties[$eventName]
    if ($null -ne $eventProperty) {
        $repairResult = Repair-HookCommand -EventValue $eventProperty.Value
        if ($repairResult.Matched) {
            if ($repairResult.Repaired) {
                $repairedEvents.Add($eventName)
            }
            else {
                $existingEvents.Add($eventName)
            }
            continue
        }
    }

    $handler = [ordered]@{
        type = 'command'
        command = $HookCommand
        commandWindows = $HookCommandWindows
        timeout = 1
    }

    $newGroup = [pscustomobject]@{
        hooks = @([pscustomobject]$handler)
    }

    if ($null -eq $eventProperty) {
        Add-Member -InputObject $config.hooks -MemberType NoteProperty -Name $eventName -Value @($newGroup)
    }
    else {
        $eventProperty.Value = @($eventProperty.Value) + @($newGroup)
    }

    $addedEvents.Add($eventName)
}

Write-Output "Config: $configPath"
Write-Output "Mode: $(if ($Apply) { 'APPLY' } else { 'DRY-RUN' })"
Write-Output "CommandWindows: $HookCommandWindows"
Write-Output "Added events: $(if ($addedEvents.Count -gt 0) { $addedEvents -join ', ' } else { '(none)' })"
Write-Output "Existing events: $(if ($existingEvents.Count -gt 0) { $existingEvents -join ', ' } else { '(none)' })"
Write-Output "Repaired async events: $(if ($repairedEvents.Count -gt 0) { $repairedEvents -join ', ' } else { '(none)' })"
Write-Output "Removed old command events: $(if ($removedEvents.Count -gt 0) { $removedEvents -join ', ' } else { '(none)' })"

if (-not $Apply) {
    Write-Output 'No file was changed. Re-run with -Apply after reviewing the command and event list.'
    exit 0
}

$configDirectory = Split-Path -Parent $configPath
if (-not (Test-Path -LiteralPath $configDirectory -PathType Container)) {
    throw "Codex home does not exist: $configDirectory"
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = "$configPath.backup-$timestamp"
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    Copy-Item -LiteralPath $configPath -Destination $backupPath
    Write-Output "Backup: $backupPath"
}

$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding utf8

$written = (Get-Content -LiteralPath $configPath -Raw -Encoding utf8) | ConvertFrom-Json
foreach ($eventName in $Events) {
    $eventProperty = $written.hooks.PSObject.Properties[$eventName]
    if ($null -eq $eventProperty) {
        throw "Verification failed for event: $eventName"
    }

    $writtenResult = Repair-HookCommand -EventValue $eventProperty.Value
    if (-not $writtenResult.Matched -or $writtenResult.Repaired) {
        throw "Verification failed for synchronous event: $eventName"
    }

    if (-not [string]::IsNullOrWhiteSpace($RemoveHookCommandWindows)) {
        foreach ($group in @($eventProperty.Value)) {
            foreach ($handler in @($group.hooks)) {
                foreach ($propertyName in @('commandWindows', 'command')) {
                    $property = $handler.PSObject.Properties[$propertyName]
                    if ($null -ne $property -and $property.Value -eq $RemoveHookCommandWindows) {
                        throw "Verification failed because the old command remains for event: $eventName"
                    }
                }
            }
        }
    }
}

Write-Output 'Configuration was written and verified.'
