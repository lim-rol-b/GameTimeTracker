param(
    [int]$ProcessId = 0,
    [ValidateRange(5,3600)][int]$Seconds = 30,
    [string]$Output = 'artifacts/memory.csv'
)
$ErrorActionPreference = 'Stop'
if ($ProcessId -eq 0) {
    $matches = @(Get-Process GameActivityTracker -ErrorAction Stop)
    if ($matches.Count -ne 1) { throw 'Specify -ProcessId when multiple instances exist.' }
    $ProcessId = $matches[0].Id
}
$rows = for ($i=0; $i -lt $Seconds; $i++) {
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    [pscustomobject]@{
        Timestamp = (Get-Date).ToString('o')
        WorkingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 2)
        PrivateBytesMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        CpuSeconds = $process.TotalProcessorTime.TotalSeconds
        Handles = $process.HandleCount
        Threads = $process.Threads.Count
    }
    $process.Dispose()
    Start-Sleep -Seconds 1
}
$parent = Split-Path $Output
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$rows | Export-Csv -Path $Output -NoTypeInformation -Encoding UTF8
$rows | Measure-Object WorkingSetMiB,PrivateBytesMiB,Handles,Threads -Minimum -Maximum -Average | Format-Table
Write-Host "CPU seconds during sample: $($rows[-1].CpuSeconds - $rows[0].CpuSeconds). Raw samples: $Output"
