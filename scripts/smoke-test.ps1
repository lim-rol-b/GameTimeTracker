param([string]$Executable = (Join-Path $PSScriptRoot '../artifacts/win-x64/GameActivityTracker.exe'))
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable).Path
$runtime = Join-Path (Split-Path $exe) 'runtime'
foreach ($required in @('GameActivityTracker.dll', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'e_sqlite3.dll')) {
    if (-not (Test-Path (Join-Path $runtime $required))) { throw "Missing runtime dependency: $required" }
}
$previousExtraction = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$extractionProbe = Join-Path $env:TEMP ([guid]::NewGuid().ToString())
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extractionProbe
try {
    # Start outside the installation folder to verify executable-relative dependency lookup.
    $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -WorkingDirectory $env:TEMP -PassThru
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        throw 'Startup smoke test timed out. Check %TEMP%/GameActivityTracker.SmokeTest/logs.'
    }
    if ($process.ExitCode -ne 0) { throw "Startup smoke test failed: exit $($process.ExitCode)" }
    $db = Join-Path $env:TEMP 'GameActivityTracker.SmokeTest/activity.db'
    if (-not (Test-Path $db)) { throw 'SQLite initialization did not produce a database.' }
    if (Test-Path $extractionProbe) { throw 'Unexpected single-file extraction cache was created.' }
} finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtraction
}
Write-Host "PASS: WPF startup, SQLite, dispatcher, tracking timer, and orderly exit. Database: $db"
