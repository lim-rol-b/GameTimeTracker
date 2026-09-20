param([string]$Executable = (Join-Path $PSScriptRoot '../artifacts/win-x64/GameActivityTracker.exe'))
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable).Path
$runtime = Join-Path (Split-Path $exe) 'runtime'
$config = Get-Content (Join-Path $runtime 'GameActivityTracker.runtimeconfig.json') -Raw | ConvertFrom-Json
$dependencies = @('GameActivityTracker.dll', 'e_sqlite3.dll')
if ($config.runtimeOptions.includedFrameworks) { $dependencies += @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll') }
foreach ($required in $dependencies) {
    if (-not (Test-Path (Join-Path $runtime $required))) { throw "Missing runtime dependency: $required" }
}
$previousExtraction = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$extractionProbe = Join-Path $env:TEMP ([guid]::NewGuid().ToString())
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extractionProbe
try {
    # Start outside the installation folder to verify executable-relative dependency lookup.
    foreach ($arguments in @(@('--smoke-test'), @('--smoke-test', '--background'))) {
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $env:TEMP -PassThru
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            throw 'Startup smoke test timed out. Check %TEMP%/GameActivityTracker.SmokeTest/logs.'
        }
        if ($process.ExitCode -ne 0) { throw "Startup smoke test failed: exit $($process.ExitCode)" }
    }
    $db = Join-Path $env:TEMP 'GameActivityTracker.SmokeTest/activity.db'
    if (-not (Test-Path $db)) { throw 'SQLite initialization did not produce a database.' }
    if (Test-Path $extractionProbe) { throw 'Unexpected single-file extraction cache was created.' }
} catch {
    $logDirectory = Join-Path $env:TEMP 'GameActivityTracker.SmokeTest/logs'
    Get-ChildItem $logDirectory -Filter 'tracker-*.log' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content $_.FullName -Tail 80 -ErrorAction SilentlyContinue }
    throw
} finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtraction
}
Write-Host "PASS: WPF startup, SQLite, dispatcher, tracking timer, and orderly exit. Database: $db"
