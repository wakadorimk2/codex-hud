[CmdletBinding()]
param(
    [string]$CodexHomePath,

    [string]$LocalApplicationDataPath,

    [string]$StartMenuProgramsPath,

    [switch]$SkipLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$sourceAppPath = Join-Path $packageRoot 'app'
$sourceExePath = Join-Path $sourceAppPath 'CodexHud.exe'
$hookScriptPath = Join-Path $packageRoot 'install-hooks.ps1'
$events = @(
    'SessionStart',
    'UserPromptSubmit',
    'PermissionRequest',
    'Stop',
    'SessionEnd'
)

if (-not (Test-Path -LiteralPath $sourceExePath -PathType Leaf)) {
    throw "Package EXE was not found: $sourceExePath"
}

if (-not (Test-Path -LiteralPath $hookScriptPath -PathType Leaf)) {
    throw "Package Hook script was not found: $hookScriptPath"
}

if ([string]::IsNullOrWhiteSpace($LocalApplicationDataPath)) {
    $LocalApplicationDataPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
}

if ([string]::IsNullOrWhiteSpace($LocalApplicationDataPath)) {
    throw 'LocalApplicationData could not be resolved.'
}

if ([string]::IsNullOrWhiteSpace($StartMenuProgramsPath)) {
    $StartMenuProgramsPath = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)
    ) 'Programs'
}

if ([string]::IsNullOrWhiteSpace($CodexHomePath)) {
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $CodexHomePath = $env:CODEX_HOME
    }
    else {
        $CodexHomePath = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
    }
}

$installRootPath = Join-Path $LocalApplicationDataPath 'CodexHud'
$installAppPath = Join-Path $installRootPath 'App'
$installExePath = Join-Path $installAppPath 'CodexHud.exe'
$shortcutPath = Join-Path $StartMenuProgramsPath 'Codex HUD.lnk'
$hooksPath = Join-Path $CodexHomePath 'hooks.json'
$newHookCommand = '"{0}" --hook' -f $installExePath

function Get-HooksConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            hooks = [pscustomobject]@{}
        }
    }

    $text = Get-Content -LiteralPath $Path -Raw -Encoding utf8
    if ([string]::IsNullOrWhiteSpace($text)) {
        return [pscustomobject]@{
            hooks = [pscustomobject]@{}
        }
    }

    $configuration = $text | ConvertFrom-Json
    if ($null -eq $configuration) {
        return [pscustomobject]@{
            hooks = [pscustomobject]@{}
        }
    }

    if ($null -eq $configuration.PSObject.Properties['hooks']) {
        Add-Member -InputObject $configuration -MemberType NoteProperty -Name 'hooks' -Value ([pscustomobject]@{})
    }

    if ($null -eq $configuration.hooks) {
        $configuration.hooks = [pscustomobject]@{}
    }

    return $configuration
}

function Get-ConfiguredHookCommands {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [Parameter(Mandatory = $true)]
        [string[]]$EventNames
    )

    $commands = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $Configuration.PSObject.Properties['hooks'] -or $null -eq $Configuration.hooks) {
        return $commands.ToArray()
    }

    foreach ($eventName in $EventNames) {
        $eventProperty = $Configuration.hooks.PSObject.Properties[$eventName]
        if ($null -eq $eventProperty) {
            continue
        }

        foreach ($group in @($eventProperty.Value)) {
            if ($null -eq $group -or $null -eq $group.PSObject.Properties['hooks']) {
                continue
            }

            foreach ($handler in @($group.hooks)) {
                if ($null -eq $handler) {
                    continue
                }

                foreach ($propertyName in @('commandWindows', 'command')) {
                    $commandProperty = $handler.PSObject.Properties[$propertyName]
                    if ($null -eq $commandProperty -or $commandProperty.Value -isnot [string]) {
                        continue
                    }

                    $command = $commandProperty.Value
                    if (-not $commands.Contains($command)) {
                        $commands.Add($command)
                    }
                }
            }
        }
    }

    return $commands.ToArray()
}

function Test-CodexHudHookCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command
    )

    return [regex]::IsMatch($Command, '(?i)CodexHud\.exe["'']?\s+--hook(?:\s|$)')
}

function Test-ExactHookCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Commands,

        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    return $Commands -contains $Target
}

function Invoke-HookScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$HookPath,

        [Parameter(Mandatory = $true)]
        [string]$HooksHome,

        [Parameter(Mandatory = $true)]
        [string]$Command,

        [string]$RemoveCommand,

        [switch]$Apply
    )

    if (-not [string]::IsNullOrWhiteSpace($RemoveCommand) -and $Apply) {
        & $HookPath -HookCommandWindows $Command -HookCommand $Command -CodexHomePath $HooksHome -RemoveHookCommandWindows $RemoveCommand -Apply
    }
    elseif (-not [string]::IsNullOrWhiteSpace($RemoveCommand)) {
        & $HookPath -HookCommandWindows $Command -HookCommand $Command -CodexHomePath $HooksHome -RemoveHookCommandWindows $RemoveCommand
    }
    elseif ($Apply) {
        & $HookPath -HookCommandWindows $Command -HookCommand $Command -CodexHomePath $HooksHome -Apply
    }
    else {
        & $HookPath -HookCommandWindows $Command -HookCommand $Command -CodexHomePath $HooksHome
    }

    if (-not $?) {
        throw 'Hook script failed.'
    }
}

function Update-StartMenuShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Description = 'Codex HUDの状態を表示します。'
    $shortcut.Save()
}

$configuration = Get-HooksConfiguration -Path $hooksPath
$configuredCommands = @(Get-ConfiguredHookCommands -Configuration $configuration -EventNames $events)
$oldCommands = @($configuredCommands | Where-Object {
    (Test-CodexHudHookCommand -Command $_) -and $_ -ne $newHookCommand
})
$oldCommands = @($oldCommands | Sort-Object -Unique)
$newHookExists = Test-ExactHookCommand -Commands $configuredCommands -Target $newHookCommand

Write-Output "Package: $packageRoot"
Write-Output "Install EXE: $installExePath"
Write-Output "State files are kept outside the app directory: $installRootPath\position.json and $installRootPath\sessions.json"

if (Test-Path -LiteralPath $installAppPath -PathType Leaf) {
    throw "The install path is a file, not a directory: $installAppPath"
}

foreach ($existingPath in @($installRootPath, $installAppPath)) {
    if (-not (Test-Path -LiteralPath $existingPath)) {
        continue
    }

    $existingItem = Get-Item -LiteralPath $existingPath
    if (-not $existingItem.PSIsContainer) {
        throw "The install path is a file, not a directory: $existingPath"
    }

    if (($existingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The install path is a reparse point: $existingPath"
    }
}

New-Item -ItemType Directory -Path $installAppPath -Force | Out-Null
foreach ($item in @(Get-ChildItem -LiteralPath $sourceAppPath -Force)) {
    Copy-Item -LiteralPath $item.FullName -Destination $installAppPath -Recurse -Force
}

if (-not (Test-Path -LiteralPath $installExePath -PathType Leaf)) {
    throw "The installed EXE was not found: $installExePath"
}

Update-StartMenuShortcut -Path $shortcutPath -TargetPath $installExePath -WorkingDirectory $installAppPath
Write-Output "Start menu shortcut updated: $shortcutPath"

Write-Output "Hook dry-run for: $hooksPath"
$dryRunRemoveCommand = $null
if ($oldCommands.Count -eq 1) {
    $dryRunRemoveCommand = $oldCommands[0]
    Write-Output "Detected one existing Codex HUD Hook command for migration: $dryRunRemoveCommand"
}
elseif ($oldCommands.Count -gt 1) {
    Write-Warning 'Multiple existing Codex HUD Hook commands were found. The installer will not remove them automatically.'
    foreach ($oldCommand in $oldCommands) {
        Write-Warning "Existing command: $oldCommand"
    }
}

Invoke-HookScript -HookPath $hookScriptPath -HooksHome $CodexHomePath -Command $newHookCommand -RemoveCommand $dryRunRemoveCommand

$hookReady = $false
if ($oldCommands.Count -gt 1) {
    Write-Warning 'Hook changes were not applied. Remove the old commands manually, then run the installer again.'
}
elseif (-not $newHookExists -or $oldCommands.Count -eq 1) {
    $answer = Read-Host 'Apply this Hook configuration? Type Y to apply or N to cancel'
    if ($answer.Trim().ToUpperInvariant() -ne 'Y') {
        Write-Output 'Hook update was cancelled. The app and Start menu shortcut remain installed.'
        return
    }

    New-Item -ItemType Directory -Path $CodexHomePath -Force | Out-Null
    Invoke-HookScript -HookPath $hookScriptPath -HooksHome $CodexHomePath -Command $newHookCommand -RemoveCommand $dryRunRemoveCommand -Apply

    $writtenConfiguration = Get-HooksConfiguration -Path $hooksPath
    $writtenCommands = @(Get-ConfiguredHookCommands -Configuration $writtenConfiguration -EventNames $events)
    if (-not (Test-ExactHookCommand -Commands $writtenCommands -Target $newHookCommand)) {
        throw 'Hook verification failed because the installed command was not found.'
    }

    if ($oldCommands.Count -eq 1 -and (Test-ExactHookCommand -Commands $writtenCommands -Target $oldCommands[0])) {
        throw 'Hook verification failed because the old command remains.'
    }

    $hookReady = $true
}
else {
    Write-Output 'The installed Hook command is already configured. No Hook file write is required.'
    $hookReady = $true
}

if (-not $hookReady) {
    Write-Output 'The HUD was not started because Hook configuration is incomplete.'
    return
}

if ($SkipLaunch) {
    Write-Output 'HUD launch skipped by -SkipLaunch.'
}
else {
    $startedProcess = Start-Process -FilePath $installExePath -WorkingDirectory $installAppPath -PassThru
    Write-Output "HUD started. Process ID: $($startedProcess.Id)"
}

Write-Output 'Codex HUD installation completed.'
