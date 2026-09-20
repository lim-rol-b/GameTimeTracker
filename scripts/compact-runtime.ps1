param([switch]$Undo)
$ErrorActionPreference = 'Stop'
# This script is shipped at runtime/docs/. Only compress immutable application files.
$runtime = Split-Path $PSScriptRoot
if (-not (Test-Path (Join-Path $runtime 'GameActivityTracker.dll'))) {
    throw 'Run the copy shipped in the published runtime/docs directory.'
}
$root = Split-Path $runtime
$exe = Join-Path $root 'GameActivityTracker.exe'
$volume = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($runtime))
if ($volume.DriveFormat -ne 'NTFS') {
    throw "LZX compression requires NTFS; this volume uses $($volume.DriveFormat). Move the extracted app to an NTFS volume first."
}
if (Get-Process GameActivityTracker -ErrorAction SilentlyContinue) {
    throw 'Exit GameActivityTracker from its tray menu before changing compression.'
}
# LZX is transparent, lossless executable compression on Windows 10/11 NTFS.
# ZIP does not preserve this filesystem attribute; apply after extraction.
if ($Undo) {
    & compact.exe /U "/S:$runtime" /A
    if ($LASTEXITCODE -ne 0) { throw 'Runtime decompression failed.' }
    & compact.exe /U $exe
} else {
    & compact.exe /C "/S:$runtime" /EXE:LZX /A
    if ($LASTEXITCODE -ne 0) { throw 'Runtime compression failed. Verify the installation is on a writable NTFS volume.' }
    & compact.exe /C /EXE:LZX $exe
}
if ($LASTEXITCODE -ne 0) { throw 'Executable compression operation failed.' }
Write-Host 'Done. Check Size on disk in folder properties; logical file sizes stay unchanged.'
