. (Join-Path $PSScriptRoot '..\sdk\ProgressIsland.ps1')
$taskId = [guid]::NewGuid().ToString('N')
try {
    foreach ($index in 1..40) {
        Send-IslandProgress -Id $taskId -Title 'PowerShell · 批量工作示例' -Progress ($index / 40 * 100) -Message "处理第 $index / 40 项"
        Start-Sleep -Milliseconds 200
    }
    Send-IslandProgress -Id $taskId -Title 'PowerShell · 批量工作示例' -Status completed -Message '所有工作已完成'
} catch {
    Send-IslandProgress -Id $taskId -Title 'PowerShell · 批量工作示例' -Status failed -Message $_.Exception.Message
    throw
}
