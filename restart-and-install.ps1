# Rebuild is done by the caller. This script closes the game, installs the
# staged DLL, then relaunches the game.
#
# Usage: powershell -ExecutionPolicy Bypass -File restart-and-install.ps1
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$gameDirectory = 'D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice'
$projectDirectory = $PSScriptRoot
$gameExe = Join-Path $gameDirectory 'A Dance of Fire and Ice.exe'
$processName = 'A Dance of Fire and Ice'
$logPath = Join-Path $projectDirectory 'install-result.log'

function Write-Step([string]$message) {
    $line = '{0} {1}' -f (Get-Date -Format o), $message
    $line | Add-Content -LiteralPath $logPath -Encoding UTF8
    Write-Host $line
}

function Get-GameProcesses {
    return @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
}

if (-not $NoBuild) {
    Write-Step 'BUILDING'
    & dotnet build (Join-Path $projectDirectory 'ADOFAIEditorStudio.csproj') -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Step 'BUILD_FAILED'
        throw "dotnet build failed with exit code $LASTEXITCODE; leaving the game untouched"
    }
}

# Refuse to deploy a stale binary: if the build did not produce a newer DLL than the
# one already staged, the install would silently ship the previous version.
$sourceDll = Join-Path $projectDirectory 'bin\Release\ADOFAIEditorStudio.dll'
if (-not (Test-Path -LiteralPath $sourceDll)) {
    Write-Step 'BUILD_OUTPUT_MISSING'
    throw "Build output not found: $sourceDll"
}

# Close the game so the mod DLL is no longer locked. Unity can hold the mapped
# assembly briefly after the process is gone, hence the settle delay.
$existing = Get-GameProcesses
if ($existing.Count -gt 0) {
    Write-Step ("CLOSING {0} process(es)" -f $existing.Count)
    $existing | Stop-Process -Force
    do {
        Start-Sleep -Milliseconds 600
    } while ((Get-GameProcesses).Count -gt 0)
    Start-Sleep -Milliseconds 1500
    Write-Step 'CLOSED'
}

# Reuse the existing installer for staging, backup, hash verification and the
# Settings.xml preservation rules. The game is already down, so it installs
# immediately instead of waiting.
Write-Step 'INSTALLING'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectDirectory 'install-after-game-exit.ps1') -NoBuild
if ($LASTEXITCODE -ne 0) {
    throw "install-after-game-exit.ps1 failed with exit code $LASTEXITCODE"
}

# Relaunch so the next test round can start immediately.
Write-Step 'RELAUNCHING'
Start-Process -FilePath $gameExe -WorkingDirectory $gameDirectory
Start-Sleep -Seconds 2
$running = Get-GameProcesses
Write-Step ("DONE gameRunning={0}" -f ($running.Count -gt 0))
