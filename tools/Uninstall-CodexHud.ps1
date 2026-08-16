[CmdletBinding()]
param(
    [string]$LocalApplicationDataPath,

    [string]$StartMenuProgramsPath,

    [switch]$SkipAppRemoval
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($LocalApplicationDataPath)) {
    $LocalApplicationDataPath = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
}

if ([string]::IsNullOrWhiteSpace($LocalApplicationDataPath)) {
    throw 'LocalApplicationData could not be resolved.'
}

if ([string]::IsNullOrWhiteSpace($StartMenuProgramsPath)) {
    $StartMenuProgramsPath = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)
    ) 'Programs'
}

$installRootPath = Join-Path $LocalApplicationDataPath 'CodexHud'
$installAppPath = Join-Path $installRootPath 'App'
$installExePath = Join-Path $installAppPath 'CodexHud.exe'
$shortcutPath = Join-Path $StartMenuProgramsPath 'Codex HUD.lnk'

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

    $expectedPath = [IO.Path]::GetFullPath((Join-Path $LocalApplicationDataPath 'CodexHud\App'))
    $actualPath = [IO.Path]::GetFullPath($Path)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($expectedPath, $actualPath)) {
        throw "Refusing to remove an unexpected path: $actualPath"
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
    Write-Output "Removed install app directory: $Path"
}

Write-Output 'Hook configuration was not read or changed.'
Remove-InstalledShortcutIfOwned -Path $shortcutPath -ExpectedTarget $installExePath

if (-not $SkipAppRemoval) {
    Remove-InstalledApp -Path $installAppPath
}
else {
    Write-Output 'App removal skipped by -SkipAppRemoval.'
}

Write-Output 'Codex HUD uninstall completed.'
