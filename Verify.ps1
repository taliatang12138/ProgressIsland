$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'src\ProgressIsland.App\ProgressIsland.App.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败' }
$exe = Join-Path $PSScriptRoot 'src\ProgressIsland.App\bin\Release\net9.0-windows\ProgressIsland.exe'
$bundledPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
if (-not (Test-Path -LiteralPath $bundledPython)) { $bundledPython = '' }
dotnet run --project (Join-Path $PSScriptRoot 'tests\ProgressIsland.Tests\ProgressIsland.Tests.csproj') -c Release --no-launch-profile -- $exe $bundledPython (Join-Path $PSScriptRoot 'sdk')
if ($LASTEXITCODE -ne 0) { throw '验证失败' }
$uiReport = Join-Path $PSScriptRoot 'artifacts\dock-ui-verification.json'
$uiCheck = Start-Process -FilePath $exe -ArgumentList ('--verify-ui "' + $uiReport + '"') -WindowStyle Hidden -PassThru
if (-not $uiCheck.WaitForExit(30000)) { Stop-Process -Id $uiCheck.Id; throw '四边停靠 UI 验证超时' }
if ($uiCheck.ExitCode -ne 0) { throw '四边停靠 UI 验证失败' }
$uiResult = Get-Content -LiteralPath $uiReport -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Output ("UI: " + $uiResult.checks.Count + " checks passed; frame gap p95=" + [math]::Round($uiResult.p95FrameGapMs, 1) + "ms")
