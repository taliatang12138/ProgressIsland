$ErrorActionPreference = 'Stop'
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\ProgressIsland.App\ProgressIsland.App.csproj') -Raw
$version = $projectXml.Project.PropertyGroup.Version
$artifactsRoot = Join-Path $PSScriptRoot 'artifacts'
$packageRoot = Join-Path $artifactsRoot ('ProgressIsland-' + $version + '-win-x64')
$published = Join-Path $artifactsRoot 'app\ProgressIsland.exe'
if (-not (Test-Path -LiteralPath $published)) { throw 'Run Build.ps1 -Portable first.' }
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'app'), (Join-Path $packageRoot 'artifacts') | Out-Null
$packageExe = Join-Path $packageRoot 'app\ProgressIsland.exe'
if (-not (Test-Path -LiteralPath $packageExe) -or
    (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $packageExe -Algorithm SHA256).Hash) {
    Copy-Item -LiteralPath $published -Destination $packageExe -Force
}
foreach ($folder in @('sdk', 'examples', 'docs')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot $folder) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $folder) -File | Copy-Item -Destination (Join-Path $packageRoot $folder) -Force
}
$imageSource = Join-Path $PSScriptRoot 'docs\images'
if (Test-Path -LiteralPath $imageSource) {
    $imageTarget = Join-Path $packageRoot 'docs\images'
    New-Item -ItemType Directory -Force -Path $imageTarget | Out-Null
    Get-ChildItem -LiteralPath $imageSource -File | Copy-Item -Destination $imageTarget -Force
}
foreach ($file in @('README.md', 'LICENSE', 'Start-ProgressIsland.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $packageRoot -Force
}
foreach ($file in @('preview.png', 'codex-ended.png', 'group-idle.png', 'overview-compact.png', 'overview-dock-left.png', 'inline-custom-idle.png', 'dock-ui-verification.json')) {
    Copy-Item -LiteralPath (Join-Path $artifactsRoot $file) -Destination (Join-Path $packageRoot 'artifacts') -Force
}
$localRuntimeReport = Join-Path $packageRoot 'artifacts\codex-package-verification.json'
if (Test-Path -LiteralPath $localRuntimeReport -PathType Leaf) {
    Remove-Item -LiteralPath $localRuntimeReport
}
$zipPath = $packageRoot + '.zip'
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal -Force
Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
