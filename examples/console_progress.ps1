# Launch this script from the island's Run command tab; it needs no SDK.
foreach ($index in 1..30) {
    Write-Output ('{"progress":' + [int]($index / 30 * 100) + ',"message":"Step ' + $index + '/30"}')
    Start-Sleep -Milliseconds 250
}
