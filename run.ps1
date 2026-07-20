# Build and (re)launch ClaudeBar in the background for development.
# Windows equivalent of the macOS run.sh: kills the running instance, recompiles, relaunches.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\ClaudeBar\ClaudeBar.csproj'

Write-Host 'Stopping any running ClaudeBar…'
Get-Process ClaudeBar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

Write-Host 'Building (Debug)…'
dotnet build $proj -c Debug -v m
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$exe = Join-Path $root 'src\ClaudeBar\bin\Debug\net8.0-windows\win-x64\ClaudeBar.exe'
if (-not (Test-Path $exe)) {
    # Non-single-file debug output path (framework-dependent build).
    $exe = Join-Path $root 'src\ClaudeBar\bin\Debug\net8.0-windows\ClaudeBar.exe'
}
Write-Host "Launching $exe"
Start-Process $exe
Write-Host 'ClaudeBar is running in the system tray.'
