function Send-IslandProgress {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Title,
        [ValidateRange(0,100)][double]$Progress,
        [string]$Message = '',
        [ValidateSet('running','paused','waiting','completed','failed')][string]$Status = 'running'
    )
    $taskDataRoot = if ($env:PROGRESS_ISLAND_DATA) { $env:PROGRESS_ISLAND_DATA } else { Join-Path $env:LOCALAPPDATA 'ProgressIsland' }
    try {
        $cfg = Get-Content -LiteralPath (Join-Path $taskDataRoot 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $payload = @{id=$Id; title=$Title; detail=$Message; status=$Status}
        if ($PSBoundParameters.ContainsKey('Progress')) { $payload.progress = $Progress }
        $body = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))
        $null = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($cfg.port)/tasks" -Headers @{Authorization="Bearer $($cfg.token)"} -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 2
    } catch { Write-Warning '任务岛上报失败，工作继续；请确认任务岛已启动。' }
}
