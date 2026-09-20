param([switch]$FrameworkDependent)
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
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $packageName = if ($FrameworkDependent) { 'win-x64-lite' } else { 'win-x64' }
    dotnet publish src/GameActivityTracker.Windows -c Release -r win-x64 --self-contained $selfContained -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $files = @(Get-ChildItem $staging -File -Force)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'GameActivityTracker.exe') {
        throw 'Expected only the app executable at the package root'
    }
    $dependencies = @('GameActivityTracker.dll', 'GameActivityTracker.runtimeconfig.json', 'e_sqlite3.dll')
    if (-not $FrameworkDependent) { $dependencies += @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll') }
    foreach ($required in $dependencies) {
        if (-not (Test-Path "$staging/runtime/$required")) { throw "Missing runtime dependency: $required" }
    }
    New-Item -ItemType Directory -Path "$staging/runtime/docs" | Out-Null
    Copy-Item scripts/compact-runtime.ps1 "$staging/runtime/docs/compact-runtime.ps1"
    if ($FrameworkDependent) {
        Copy-Item scripts/LITE-README.txt "$staging/runtime/docs/LITE-README.txt"
    }
    $readme = Get-Content scripts/PORTABLE-README.txt -Raw -Encoding UTF8
    if ($FrameworkDependent) {
        $readme = $readme.Replace('No .NET runtime installation required.', 'Requires .NET 8 Desktop Runtime (Windows x64); see LITE-README.txt.')
    }
    Set-Content "$staging/runtime/docs/README.txt" -Value $readme -Encoding UTF8
    Copy-Item UPGRADE.md "$staging/runtime/docs/UPGRADE.md"
    Copy-Item PERFORMANCE.md "$staging/runtime/docs/PERFORMANCE.md"
    Copy-Item scripts/measure-memory.ps1 "$staging/runtime/docs/measure-memory.ps1"
    Copy-Item LICENSE "$staging/runtime/docs/LICENSE"
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    $destination = "artifacts/$packageName"
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    Move-Item $staging $destination
    Compress-Archive -Path "$destination/*" -DestinationPath "artifacts/GameActivityTracker-$packageName.zip" -CompressionLevel Optimal -Force
    $bytes = (Get-ChildItem $destination -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host "Package: artifacts/GameActivityTracker-$packageName.zip; unpacked: $bytes bytes"
} finally {
    if ($staging -and (Test-Path $staging)) { Remove-Item $staging -Recurse -Force }
    Pop-Location
}
