# Publish a self-contained, single-file ClaudeBar.exe (no .NET runtime required on the target)
# and zip it into dist\ as the release asset the auto-updater expects.
param(
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\ClaudeBar\ClaudeBar.csproj'
$outDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host 'Publishing self-contained single-file win-x64…'
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $outDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$exe = Join-Path $outDir 'ClaudeBar.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe not found" }

if (-not $Version) {
    $Version = (Get-Item $exe).VersionInfo.ProductVersion
    if (-not $Version) { $Version = 'dev' }
}

# WPF self-contained single-file still leaves a handful of native DLLs (wpfgfx_cor3.dll,
# PresentationNative_cor3.dll, D3DCompiler_47_cor3.dll, …) unembedded — the SDK excludes them
# from the bundle. They must ship alongside ClaudeBar.exe, so zip the whole folder (minus the .pdb).
$zip = Join-Path $distDir "ClaudeBar-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$payload = Get-ChildItem $outDir -File | Where-Object { $_.Extension -ne '.pdb' }
Compress-Archive -Path $payload.FullName -DestinationPath $zip
Write-Host "Wrote $zip ($($payload.Count) files)"
