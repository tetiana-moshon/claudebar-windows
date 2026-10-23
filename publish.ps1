# Publish a self-contained, single-file ClaudeBar.exe (no .NET runtime required on the target)
# and zip it into dist\ as the release asset the auto-updater expects.
#   ./publish.ps1 -Version 1.1.2   release build, stamped 1.1.2 (attach this zip to tag v1.1.2)
#   ./publish.ps1                  dev build, keeps the csproj's "X.Y.Z-dev" — never ship or install it
param(
    [ValidatePattern('^(\d+\.\d+\.\d+)?$')]
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\ClaudeBar\ClaudeBar.csproj'
$outDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# The release number exists only here: a build that ships must not share its number with any
# earlier build, or the auto-updater (which compares numbers) would treat them as the same.
$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

Write-Host 'Publishing self-contained single-file win-x64…'
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true $versionArgs -o $outDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$exe = Join-Path $outDir 'ClaudeBar.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe not found" }

# ProductVersion can carry a "+<gitsha>" SourceLink suffix; keep just the semver.
$built = (Get-Item $exe).VersionInfo.ProductVersion
$built = if ($built) { ($built -split '\+')[0] } else { 'dev' }
if ($Version -and $built -ne $Version) { throw "Built $built, expected $Version" }
if (-not $Version) { Write-Warning "Dev build $built — not a release; pass -Version X.Y.Z to ship one." }

# WPF self-contained single-file still leaves a handful of native DLLs (wpfgfx_cor3.dll,
# PresentationNative_cor3.dll, D3DCompiler_47_cor3.dll, …) unembedded — the SDK excludes them
# from the bundle. They must ship alongside ClaudeBar.exe, so zip the whole folder (minus the .pdb).
$zip = Join-Path $distDir "ClaudeBar-$built.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$payload = Get-ChildItem $outDir -File | Where-Object { $_.Extension -ne '.pdb' }
Compress-Archive -Path $payload.FullName -DestinationPath $zip
Write-Host "Wrote $zip ($($payload.Count) files)"
