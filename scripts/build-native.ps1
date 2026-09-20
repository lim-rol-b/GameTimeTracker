$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    cargo test --manifest-path native/Cargo.toml --workspace
    if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed' }
    cargo build --release --manifest-path native/Cargo.toml -p gat-app
    if ($LASTEXITCODE -ne 0) { throw 'Rust build failed' }
    $binary = Join-Path (Get-Location) 'native/target/release/gat.exe'
    if (-not (Test-Path $binary)) { $binary = Join-Path (Get-Location) 'native/target/release/gat' }
    if (-not (Test-Path $binary)) { throw 'gat binary not found under native/target/release' }
    New-Item -ItemType Directory -Path 'artifacts/native' -Force | Out-Null
    Copy-Item $binary 'artifacts/native/gat.exe' -Force
    $icon = Join-Path (Get-Location) 'src/GameActivityTracker.Windows/Assets/App.ico'
    if (Test-Path $icon) { Copy-Item $icon 'artifacts/native/App.ico' -Force }
    Write-Host 'Native lightweight engine: artifacts/native/gat.exe'
} finally {
    Pop-Location
}
