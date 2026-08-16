[CmdletBinding()]
param(
    [string]$LocalApplicationDataPath,

    [string]$StartMenuProgramsPath,

    [switch]$SkipLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$sourceAppPath = Join-Path $packageRoot 'app'
$sourceExePath = Join-Path $sourceAppPath 'CodexHud.exe'

if (-not (Test-Path -LiteralPath $sourceExePath -PathType Leaf)) {
    throw "Package EXE was not found: $sourceExePath"
}

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

New-Item -ItemType Directory -Path $installAppPath -Force | Out-Null
foreach ($item in @(Get-ChildItem -LiteralPath $sourceAppPath -Force)) {
    Copy-Item -LiteralPath $item.FullName -Destination $installAppPath -Recurse -Force
}

if (-not (Test-Path -LiteralPath $installExePath -PathType Leaf)) {
    throw "The installed EXE was not found: $installExePath"
}

Update-StartMenuShortcut `
    -Path $shortcutPath `
    -TargetPath $installExePath `
    -WorkingDirectory $installAppPath
Write-Output "Start menu shortcut updated: $shortcutPath"
Write-Output 'Hook configuration was not read or changed.'

if ($SkipLaunch) {
    Write-Output 'HUD launch skipped by -SkipLaunch.'
}
else {
    $startedProcess = Start-Process `
        -FilePath $installExePath `
        -WorkingDirectory $installAppPath `
        -PassThru
    Write-Output "HUD started. Process ID: $($startedProcess.Id)"
}

Write-Output 'Codex HUD installation completed.'
