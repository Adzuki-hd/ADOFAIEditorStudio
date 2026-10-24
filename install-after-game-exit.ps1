param(
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$gameDirectory = 'D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice'
$projectDirectory = $PSScriptRoot
$projectPath = Join-Path $projectDirectory 'ADOFAIEditorStudio.csproj'
$destination = Join-Path $gameDirectory 'Mods\ADOFAIEditorStudio'
$sourceDll = Join-Path $projectDirectory ("bin\{0}\ADOFAIEditorStudio.dll" -f $Configuration)
$logPath = Join-Path $projectDirectory 'install-result.log'
$mutex = New-Object System.Threading.Mutex($false, 'Local\ADOFAIEditorStudioInstallWatcher')
$ownsMutex = $false
$stagingDirectory = $null

function Write-InstallLog([string]$status, [string]$message) {
    $line = '{0} {1} {2}' -f (Get-Date -Format o), $status, $message
    $line | Add-Content -LiteralPath $logPath -Encoding UTF8
    Write-Host $line
}

function Test-GameRunning {
    $processes = @(Get-Process -Name 'A Dance of Fire and Ice' -ErrorAction SilentlyContinue)
    return $processes.Count -gt 0
}

function Wait-ForStableGameExit {
    if (Test-GameRunning) {
        Write-InstallLog 'WAITING' 'Game is running; install queued for process exit.'
    }

    do {
        while (Test-GameRunning) {
            Start-Sleep -Seconds 2
        }

        # Unity and UMM can keep the assembly mapped briefly after the main
        # process disappears. Also catch an immediate game restart.
        Start-Sleep -Milliseconds 1800
    } while (Test-GameRunning)
}

try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) {
        Write-InstallLog 'SKIPPED' 'Another install watcher is already queued.'
        exit 2
    }

    $resolvedGame = (Resolve-Path -LiteralPath $gameDirectory).Path
    if (-not (Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Write-InstallLog 'PREPARE' ("Created missing destination: {0}" -f $destination)
    }
    $resolvedDestination = (Resolve-Path -LiteralPath $destination).Path
    $expectedDestination = Join-Path $resolvedGame 'Mods\ADOFAIEditorStudio'
    if ($resolvedDestination -ne $expectedDestination) {
        throw "Unexpected destination path: $resolvedDestination"
    }

    if (-not $NoBuild) {
        Write-InstallLog 'BUILDING' ("Configuration={0}" -f $Configuration)
        & dotnet build $projectPath -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    }

    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "Build output not found: $sourceDll"
    }

    # Stage an immutable copy before waiting. A later build cannot silently
    # change what this queued watcher installs.
    $stagingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ADOFAIEditorStudio-install-{0}" -f $PID)
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    # 这几个是运行时载荷、不是构建产物，所以从打包目录拿。
    # UiFont.ttf 是子集化过的中文字体（975 圆体，见 tools/make-font-subset.py），
    # UiFont-LICENSE.txt 是它 OFL 许可的原文 —— 许可要求随字体一起分发。
    $distDirectory = Join-Path $projectDirectory 'dist\ADOFAIEditorStudio'
    # ImGui 已整体删除（2026-10-24），不再分发 cimgui.dll / ImGui.NET.dll。
    # 剩下的随包资源只有左下角那颗按钮的图标。
    $distPayload = @('AesIcon.rgba')
    # 只装运行必需的文件：README / 更新记录这类文档不随包安装。
    $payload = @('ADOFAIEditorStudio.dll', 'Info.json') + $distPayload
    foreach ($name in $payload) {
        if ($name -eq 'ADOFAIEditorStudio.dll') {
            $sourcePath = $sourceDll
        } elseif ($distPayload -contains $name) {
            $sourcePath = Join-Path $distDirectory $name
        } else {
            $sourcePath = Join-Path $projectDirectory $name
        }
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            Write-InstallLog 'STAGE_MISSING' $sourcePath
            throw "staging 源文件不存在: $sourcePath"
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $stagingDirectory $name) -Force
    }
    $queuedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $stagingDirectory 'ADOFAIEditorStudio.dll')).Hash
    Write-InstallLog 'QUEUED' ("SHA256={0}" -f $queuedHash)

    Wait-ForStableGameExit

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $resolvedDestination ('Backups\pre-auto-install-' + $stamp)
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($name in @('ADOFAIEditorStudio.dll', 'Info.json', 'README.md', 'Settings.xml')) {
        $installed = Join-Path $resolvedDestination $name
        if (Test-Path -LiteralPath $installed) {
            Copy-Item -LiteralPath $installed -Destination (Join-Path $backup $name) -Force
        }
    }

    $stagedDll = Join-Path $stagingDirectory 'ADOFAIEditorStudio.dll'
    $temporaryDll = Join-Path $resolvedDestination 'ADOFAIEditorStudio.dll.new'
    $installedDll = Join-Path $resolvedDestination 'ADOFAIEditorStudio.dll'
    $installed = $false
    for ($attempt = 1; $attempt -le 10 -and -not $installed; $attempt++) {
        try {
            if (Test-GameRunning) {
                Wait-ForStableGameExit
            }
            Copy-Item -LiteralPath $stagedDll -Destination $temporaryDll -Force
            $temporaryHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporaryDll).Hash
            if ($temporaryHash -ne $queuedHash) {
                throw "Staged copy hash mismatch: $temporaryHash"
            }
            Move-Item -LiteralPath $temporaryDll -Destination $installedDll -Force
            $installed = $true
        }
        catch {
            if ($attempt -ge 10) {
                throw
            }
            Start-Sleep -Milliseconds 750
        }
    }

    Copy-Item -LiteralPath (Join-Path $stagingDirectory 'Info.json') -Destination (Join-Path $resolvedDestination 'Info.json') -Force
    # README 不再随包安装（只发运行必需的文件），这里按需拷贝，缺了不算失败。
    $stagedReadme = Join-Path $stagingDirectory 'README.md'
    if (Test-Path -LiteralPath $stagedReadme) {
        Copy-Item -LiteralPath $stagedReadme -Destination (Join-Path $resolvedDestination 'README.md') -Force
    }
    # 其余运行时载荷一并放过去。走到这儿游戏已经退干净了，
    # 所以运行库文件不会再被进程锁住，不需要上面那套重试。
    foreach ($name in $distPayload) {
        Copy-Item -LiteralPath (Join-Path $stagingDirectory $name) -Destination (Join-Path $resolvedDestination $name) -Force
    }

    Get-ChildItem -LiteralPath $resolvedDestination -Filter 'ADOFAIEditorStudio.dll.*.cache' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    $installedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installedDll).Hash
    if ($installedHash -ne $queuedHash) {
        throw "Installed DLL hash mismatch: expected $queuedHash, got $installedHash"
    }

    Write-InstallLog 'SUCCESS' ("SHA256={0} Backup={1}" -f $installedHash, $backup)
}
catch {
    Write-InstallLog 'ERROR' $_.Exception.Message
    exit 1
}
finally {
    if ($stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory)) {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolvedStaging = [System.IO.Path]::GetFullPath($stagingDirectory)
        $stagingLeaf = Split-Path -Leaf $resolvedStaging
        $isInsideTemp = $resolvedStaging.StartsWith(
            $tempRoot,
            [System.StringComparison]::OrdinalIgnoreCase
        )
        $isExpectedLeaf = $stagingLeaf.StartsWith(
            'ADOFAIEditorStudio-install-',
            [System.StringComparison]::OrdinalIgnoreCase
        )

        if ($isInsideTemp -and $isExpectedLeaf) {
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force -ErrorAction SilentlyContinue
        }
        else {
            Write-InstallLog 'WARNING' ("Refused to remove unexpected staging path: {0}" -f $resolvedStaging)
        }
    }
    if ($ownsMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
