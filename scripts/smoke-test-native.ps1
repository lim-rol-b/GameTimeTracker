param([string]$Executable = (Join-Path $PSScriptRoot '../native/target/release/gat.exe'))
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable).Path
$data = Join-Path $env:TEMP ('GameActivityTracker.NativeSmoke-' + [guid]::NewGuid().ToString('N'))
try {
    $process = Start-Process -FilePath $exe -ArgumentList @('--smoke-test', '--smoke-seconds', '4', '--data-dir', $data) -WorkingDirectory (Split-Path $exe) -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Native smoke test failed: exit $($process.ExitCode)" }
    if (-not (Test-Path (Join-Path $data 'activity.db'))) { throw 'SQLite initialization did not produce a database.' }
    Write-Host "PASS: native lightweight engine startup, SQLite initialization, tracking loop and orderly exit. Data: $data"
} finally {
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
}
