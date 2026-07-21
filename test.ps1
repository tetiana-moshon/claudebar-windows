# Run the unit-test suite.
# The running tray app locks ClaudeBar.exe, which blocks a rebuild of the referenced project during
# `dotnet test` — so stop any running instance first. Extra args pass straight through to dotnet
# (e.g. ./test.ps1 --filter FullyQualifiedName~AutoUpdater).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'tests\ClaudeBar.Tests\ClaudeBar.Tests.csproj'

Write-Host 'Stopping any running ClaudeBar…'
Get-Process ClaudeBar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

Write-Host 'Running tests…'
dotnet test $proj @args
