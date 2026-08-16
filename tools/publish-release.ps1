[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\CodexHud\CodexHud.csproj'
$artifactsPath = Join-Path $repositoryRoot 'artifacts'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Project file was not found: $projectPath"
}

$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding utf8)
$versionNode = $projectXml.SelectSingleNode('//Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw 'The project does not define a Version.'
}

$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    throw "The project Version is not safe for an artifact name: $version"
}

$artifactName = "CodexHud-$version-win-x64"
$zipPath = Join-Path $artifactsPath "$artifactName.zip"
$stagingPath = Join-Path $artifactsPath ".${artifactName}-staging"
$appPath = Join-Path $stagingPath 'app'

New-Item -ItemType Directory -Path $artifactsPath -Force | Out-Null

try {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $appPath -Force | Out-Null

    $dotnetCommand = Get-Command dotnet.exe -ErrorAction Stop
    $publishArguments = @(
        'publish',
        $projectPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=false',
        '-o', $appPath
    )

    Write-Output "Publishing $artifactName ..."
    & $dotnetCommand.Source @publishArguments
    $publishExitCode = $LASTEXITCODE
    if ($publishExitCode -ne 0) {
        throw "dotnet publish failed with exit code $publishExitCode."
    }

    $publishedExe = Join-Path $appPath 'CodexHud.exe'
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        throw "Published EXE was not found: $publishedExe"
    }

    $nativeSkiaDlls = @(Get-ChildItem -LiteralPath $appPath -File | Where-Object {
        $_.Name -match '(?i)^libSkiaSharp.*\.dll$'
    })
    if ($nativeSkiaDlls.Count -eq 0) {
        throw 'The publish output does not contain a SkiaSharp native DLL.'
    }

    foreach ($markerName in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
        $markerPath = Join-Path $appPath $markerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "The self-contained publish marker was not found: $markerName"
        }
    }

    foreach ($fileName in @(
        'Install-CodexHud.ps1',
        'Uninstall-CodexHud.ps1',
        'INSTALL.txt'
    )) {
        $sourcePath = Join-Path $repositoryRoot "tools\$fileName"
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Package source file was not found: $sourcePath"
        }

        Copy-Item -LiteralPath $sourcePath -Destination $stagingPath -Force
    }

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "The release ZIP was not created: $zipPath"
    }

    $zipItem = Get-Item -LiteralPath $zipPath
    Write-Output "Created: $($zipItem.FullName)"
    Write-Output "Size: $($zipItem.Length) bytes"
    Write-Output "Native SkiaSharp DLLs: $($nativeSkiaDlls.Name -join ', ')"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}
