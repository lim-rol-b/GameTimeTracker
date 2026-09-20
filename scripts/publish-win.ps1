$ErrorActionPreference = 'Stop'
$staging = $null
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet restore
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test tests/GameActivityTracker.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    # Publish into a fresh staging folder so old loose dependencies cannot leak into the ZIP.
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
    dotnet publish src/GameActivityTracker.Windows -c Release -r win-x64 --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $files = @(Get-ChildItem $staging -File -Force)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'GameActivityTracker.exe') {
        throw 'Expected only the app executable at the package root'
    }
    foreach ($required in @('GameActivityTracker.dll', 'GameActivityTracker.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path "$staging/runtime/$required")) { throw "Missing runtime dependency: $required" }
    }
    New-Item -ItemType Directory -Path "$staging/runtime/docs" | Out-Null
    Copy-Item scripts/PORTABLE-README.txt "$staging/runtime/docs/README.txt"
    Copy-Item UPGRADE.md "$staging/runtime/docs/UPGRADE.md"
    Copy-Item LICENSE "$staging/runtime/docs/LICENSE"
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    if (Test-Path artifacts/win-x64) { Remove-Item artifacts/win-x64 -Recurse -Force }
    Move-Item $staging artifacts/win-x64
    Compress-Archive -Path artifacts/win-x64/* -DestinationPath artifacts/GameActivityTracker-win-x64.zip -Force
    Write-Host 'Portable package: artifacts/GameActivityTracker-win-x64.zip'
} finally {
    if ($staging -and (Test-Path $staging)) { Remove-Item $staging -Recurse -Force }
    Pop-Location
}
