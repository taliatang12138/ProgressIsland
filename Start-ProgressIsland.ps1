$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$portable = Join-Path $taskRoot 'app\ProgressIsland.exe'
$built = Join-Path $taskRoot 'src\ProgressIsland.App\bin\Release\net9.0-windows\ProgressIsland.exe'
if (Test-Path -LiteralPath $portable) {
    Start-Process -FilePath $portable -WorkingDirectory $taskRoot -WindowStyle Hidden
} else {
    if (-not (Test-Path -LiteralPath $built)) {
        dotnet build (Join-Path $taskRoot 'src\ProgressIsland.App\ProgressIsland.App.csproj') -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    }
    Start-Process -FilePath $built -WorkingDirectory $taskRoot -WindowStyle Hidden
}
