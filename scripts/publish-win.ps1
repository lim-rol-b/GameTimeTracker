$ErrorActionPreference = 'Stop'
$staging = $null
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet restore
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    # Build the native engine before publish so the package includes gat.exe at its root.
    $engine = Join-Path (Get-Location) 'native/target/release/gat.exe'
    if (-not (Test-Path $engine)) {
        if (Get-Command cargo -ErrorAction SilentlyContinue) {
            cargo build --release --manifest-path native/Cargo.toml -p gat-app
            if ($LASTEXITCODE -ne 0) { throw 'Native engine build failed' }
        } else {
            Write-Warning 'cargo not found; packaging the WPF viewer without the native engine.'
        }
    }
    # Publish into a fresh staging folder so old loose dependencies cannot leak into the ZIP.
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
    dotnet publish src/GameActivityTracker.Windows -c Release -r win-x64 --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $files = @(Get-ChildItem $staging -File -Force)
    if ($files.Name -notcontains 'GameActivityTracker.exe') { throw 'Expected the app executable at the package root' }
    $unexpected = @($files | Where-Object { $_.Name -notin @('GameActivityTracker.exe', 'gat.exe', 'App.ico') })
    if ($unexpected.Count -gt 0) { throw "Unexpected files at package root: $($unexpected.Name -join ', ')" }
    foreach ($required in @('GameActivityTracker.dll', 'GameActivityTracker.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path "$staging/runtime/$required")) { throw "Missing runtime dependency: $required" }
    }
    # Fallback copies when publish did not include them yet.
    if (Test-Path $engine) { Copy-Item $engine (Join-Path $staging 'gat.exe') -Force }
    $engineIcon = Join-Path (Get-Location) 'src/GameActivityTracker.Windows/Assets/App.ico'
    if (Test-Path $engineIcon) { Copy-Item $engineIcon (Join-Path $staging 'App.ico') -Force }
    # Defense in depth: drop framework satellite resources for every culture except the
    # one the viewer actually ships, then remove the directories left empty.
    $keepCulture = 'zh-Hans'
    $runtimeDir = Join-Path $staging 'runtime'
    $satellites = @(Get-ChildItem $runtimeDir -Recurse -File -Filter '*.resources.dll')
    $stripped = @($satellites | Where-Object { $_.Directory.Name -ne $keepCulture })
    if ($stripped.Count -gt 0) { $stripped | Remove-Item -Force }
    Get-ChildItem $runtimeDir -Directory |
        Where-Object { (Get-ChildItem $_.FullName -Recurse -File | Measure-Object).Count -eq 0 } |
        Remove-Item -Recurse -Force
    Write-Host "Stripped $($stripped.Count) unused satellite resource assemblies (kept $keepCulture)."
    New-Item -ItemType Directory -Path "$staging/runtime/docs" | Out-Null
    Copy-Item scripts/PORTABLE-README.txt "$staging/runtime/docs/README.txt"
    Copy-Item UPGRADE.md "$staging/runtime/docs/UPGRADE.md"
    Copy-Item LICENSE "$staging/runtime/docs/LICENSE"
    Copy-Item native/README.md "$staging/runtime/docs/NATIVE.md"
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    if (Test-Path artifacts/win-x64) { Remove-Item artifacts/win-x64 -Recurse -Force }
    Move-Item $staging artifacts/win-x64
    Compress-Archive -Path artifacts/win-x64/* -DestinationPath artifacts/GameActivityTracker-win-x64.zip -Force
    $unpackedMb = [math]::Round((Get-ChildItem artifacts/win-x64 -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "Portable package: artifacts/GameActivityTracker-win-x64.zip ($unpackedMb MB unpacked)"
} finally {
    if ($staging -and (Test-Path $staging)) { Remove-Item $staging -Recurse -Force }
    Pop-Location
}
