# Rolls the game back to the last known-good build.
#
# The migration work installs over the same mod ID, so the game only ever holds
# one build. This restores the archived working DLL recorded before the
# migration started, so a broken experiment is one command away from undo.
#
# Usage: powershell -ExecutionPolicy Bypass -File rollback-to-baseline.ps1
param(
    [string]$Baseline = ''
)

$ErrorActionPreference = 'Stop'

$gameDirectory = 'D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice'
$modDirectory = Join-Path $gameDirectory 'Mods\ADOFAIEditorStudio'
$projectDirectory = $PSScriptRoot

# The baseline archived by the pre-migration backup.
if ([string]::IsNullOrEmpty($Baseline)) {
    $Baseline = Join-Path $projectDirectory 'Backups\pre-evaluator-migration-20260911-210341\ADOFAIEditorStudio.rollback.dll'
}

if (-not (Test-Path -LiteralPath $Baseline)) {
    throw "Rollback baseline not found: $Baseline"
}

$expectedHash = (Get-FileHash -LiteralPath $Baseline -Algorithm SHA256).Hash
Write-Host "Baseline : $Baseline"
Write-Host "SHA256   : $expectedHash"

$processName = 'A Dance of Fire and Ice'
$running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "Closing $($running.Count) game process(es)"
    $running | Stop-Process -Force
    do { Start-Sleep -Milliseconds 600 } while (@(Get-Process -Name $processName -ErrorAction SilentlyContinue).Count -gt 0)
    Start-Sleep -Milliseconds 1500
}

# Keep whatever is currently deployed, so the experiment can be brought back.
$current = Join-Path $modDirectory 'ADOFAIEditorStudio.dll'
if (Test-Path -LiteralPath $current) {
    $displaced = Join-Path $projectDirectory ('Backups\displaced-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.dll')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $displaced) | Out-Null
    Copy-Item -LiteralPath $current -Destination $displaced -Force
    Write-Host "Displaced current build -> $displaced"
}

Copy-Item -LiteralPath $Baseline -Destination $current -Force

# Verify the deployed bytes really are the baseline rather than trusting the copy.
$deployedHash = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash
if ($deployedHash -ne $expectedHash) {
    throw "Rollback verification failed: deployed $deployedHash != baseline $expectedHash"
}

Write-Host "ROLLED BACK OK ($deployedHash)"
Start-Process -FilePath (Join-Path $gameDirectory 'A Dance of Fire and Ice.exe') -WorkingDirectory $gameDirectory
Start-Sleep -Seconds 2
Write-Host ("DONE gameRunning={0}" -f (@(Get-Process -Name $processName -ErrorAction SilentlyContinue).Count -gt 0))
