[CmdletBinding()]
param(
    [string]$CodexHomePath,

    [string]$LocalApplicationDataPath,

    [string]$StartMenuProgramsPath,

    [switch]$SkipAppRemoval
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$hookScriptPath = Join-Path $packageRoot 'install-hooks.ps1'

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
$installedHookCommand = '"{0}" --hook' -f $installExePath
$events = @(
    'SessionStart',
    'UserPromptSubmit',
    'PermissionRequest',
    'Stop',
    'SessionEnd'
)

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

function Invoke-RemoveHookScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$HookPath,

        [Parameter(Mandatory = $true)]
        [string]$HooksHome,

        [Parameter(Mandatory = $true)]
        [string]$Command,

        [switch]$Apply
    )

    if ($Apply) {
        & $HookPath -HookCommandWindows $Command -HookCommand $Command -CodexHomePath $HooksHome -RemoveOnly -Apply
    }
    else {
        & $HookPath -HookCommandWindows $Command -HookCommand $Command -CodexHomePath $HooksHome -RemoveOnly
    }

    if (-not $?) {
        throw 'Hook removal script failed.'
    }
}

function Remove-InstalledShortcutIfOwned {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedTarget
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Output "Start menu shortcut was not found: $Path"
        return
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $target = $shortcut.TargetPath
    if ([string]::IsNullOrWhiteSpace($target)) {
        Write-Warning "The shortcut has no target. It was kept: $Path"
        return
    }

    $expectedFullPath = [IO.Path]::GetFullPath($ExpectedTarget)
    $targetFullPath = [IO.Path]::GetFullPath($target)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($targetFullPath, $expectedFullPath)) {
        Write-Output "A different EXE is targeted. The shortcut was kept: $Path"
        Write-Output "Shortcut target: $targetFullPath"
        return
    }

    Remove-Item -LiteralPath $Path -Force
    Write-Output "Removed Start menu shortcut: $Path"
}

function Remove-InstalledApp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Output "Install app directory was not found: $Path"
        return
    }

    $item = Get-Item -LiteralPath $Path
    if (-not $item.PSIsContainer) {
        throw "The install app path is not a directory. It was not removed: $Path"
    }

    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The install app directory is a reparse point. It was not removed: $Path"
    }

    $expectedName = [IO.Path]::GetFullPath((Join-Path $LocalApplicationDataPath 'CodexHud\App'))
    $actualName = [IO.Path]::GetFullPath($Path)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($expectedName, $actualName)) {
        throw "Refusing to remove an unexpected path: $actualName"
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
    Write-Output "Removed install app directory: $Path"
}

$configuration = Get-HooksConfiguration -Path $hooksPath
$configuredCommands = @(Get-ConfiguredHookCommands -Configuration $configuration -EventNames $events)
$installedHookExists = $configuredCommands -contains $installedHookCommand

Write-Output "Install EXE: $installExePath"
Write-Output "Hook dry-run for: $hooksPath"
Invoke-RemoveHookScript -HookPath $hookScriptPath -HooksHome $CodexHomePath -Command $installedHookCommand

if ($installedHookExists) {
    $answer = Read-Host 'Remove the installed Codex HUD Hook? Type Y to apply or N to cancel'
    if ($answer.Trim().ToUpperInvariant() -ne 'Y') {
        Write-Output 'Uninstall was cancelled. The Hook, shortcut, and app remain installed.'
        return
    }

    New-Item -ItemType Directory -Path $CodexHomePath -Force | Out-Null
    Invoke-RemoveHookScript -HookPath $hookScriptPath -HooksHome $CodexHomePath -Command $installedHookCommand -Apply

    $writtenConfiguration = Get-HooksConfiguration -Path $hooksPath
    $writtenCommands = @(Get-ConfiguredHookCommands -Configuration $writtenConfiguration -EventNames $events)
    if ($writtenCommands -contains $installedHookCommand) {
        throw 'Hook removal verification failed because the installed command remains.'
    }
}
else {
    Write-Output 'The installed Codex HUD Hook was not found. No hooks.json write is required.'
}

Remove-InstalledShortcutIfOwned -Path $shortcutPath -ExpectedTarget $installExePath

if (-not $SkipAppRemoval) {
    Remove-InstalledApp -Path $installAppPath
}
else {
    Write-Output 'App removal skipped by -SkipAppRemoval.'
}

Write-Output 'Codex HUD uninstall completed.'
