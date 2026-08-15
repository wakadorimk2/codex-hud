[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseExe = Join-Path $repositoryRoot 'src\CodexHud\bin\Release\net10.0-windows\CodexHud.exe'
$buildCommand = 'dotnet build .\src\CodexHud\CodexHud.csproj -c Release'
$startMenuPrograms = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)
) 'Programs'
$shortcutPath = Join-Path $startMenuPrograms 'Codex HUD.lnk'

if (-not (Test-Path -LiteralPath $releaseExe -PathType Leaf)) {
    Write-Error "Release EXEがありません: $releaseExe`n先に次を実行してください: $buildCommand"
    exit 1
}

try {
    New-Item -ItemType Directory -Path $startMenuPrograms -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $releaseExe
    $shortcut.WorkingDirectory = $repositoryRoot
    $shortcut.IconLocation = "$releaseExe,0"
    $shortcut.Description = 'Codex HUDの状態を表示します。'
    $shortcut.Save()

    Write-Output "作成または更新しました: $shortcutPath"
    Write-Output "起動先: $releaseExe"
    exit 0
}
catch {
    Write-Error "スタートメニューショートカットを作成できません: $($_.Exception.Message)"
    exit 1
}
