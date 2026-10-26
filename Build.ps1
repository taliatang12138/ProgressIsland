param([switch]$Portable)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ProgressIsland.App\ProgressIsland.App.csproj'
if ($Portable) {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $PSScriptRoot 'artifacts\app') --nologo
} else { dotnet build $project -c Release --nologo }
if ($LASTEXITCODE -ne 0) { throw '构建失败' }
