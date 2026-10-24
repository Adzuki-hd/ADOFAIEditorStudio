<#
打 UMM 分发 zip。

为什么必须手写条目名：
  zip 规范（APPNOTE 4.4.17.1）规定条目名的路径分隔符只能是正斜杠 '/'。
  用反斜杠打出来的包，UMM 照样能装 —— Windows 把 '\' 也当路径分隔符。
  但 AdofaiModManager 的解压逻辑是：

      string text  = fullName.Substring(0, fullName.Length - "Info.json".Length); // 未归一化，带 '\'
      string text3 = entry.FullName.Replace('\\', '/');                            // 归一化成 '/'
      if (!text3.StartsWith(text)) continue;                                       // 带 '\' 的前缀 → 永远不匹配

  前缀带'\'、比较串带'/'，StartsWith 全失败，于是它 continue 掉每一个文件，
  建一个空文件夹、还返回"安装成功"。现象就是：装完 Mods/<Mod>/ 是空的。

**包内条目走白名单（2026-10-16 改）：**
  原来这里是"把源目录里的文件全打进去，只排除 .exe"。那条排除的由来是
  "源目录里有个 ADOFAIEditorStudio.exe，Defender 还拦它" —— 后来查清那个 exe
  根本不是我们的工具，**是自我复制的蠕虫**（钻进每个文件夹、放一个和文件夹同名的 .exe）。
  当时把它当旧工具排除了事，等于按下症状、没管根因。
  而且"枚举目录"这个动作本身就不安全：源目录里冒出任何东西都会被原样打给玩家 ——
  上次没出事，只是因为蠕虫恰好落在 dist\ 而不是 dist\ADOFAIEditorStudio\。

  现在**只打白名单里的文件**：少一个直接失败；目录里多出来的任何东西根本进不来；
  打完再逐条核对一次条目名（多一个也失败）。

用法：
  powershell -ExecutionPolicy Bypass -File pack-release.ps1 -Version 0.29.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$SourceDir,
    [string]$OutDir,
    [string]$ModName = 'ADOFAIEditorStudio'
)

$ErrorActionPreference = 'Stop'

if (-not $SourceDir) { $SourceDir = Join-Path $PSScriptRoot ("dist\" + $ModName) }
if (-not $OutDir)    { $OutDir    = Join-Path $PSScriptRoot 'dist' }

if (-not (Test-Path -LiteralPath $SourceDir)) {
    throw "打包源目录不存在: $SourceDir"
}
$info = Join-Path $SourceDir 'Info.json'
if (-not (Test-Path -LiteralPath $info)) {
    throw "源目录里没有 Info.json（UMM 包必须带它）: $info"
}

# 程序集名从 Info.json 取，不在脚本里写第二份 —— 两处写死迟早不一致。
# ASCII 名字（纯字母数字点下划线）之外一律拒绝：包内条目名来自它，
# 放进来奇怪的东西就等于往包里塞奇怪的东西。
$assemblyName = (Get-Content -LiteralPath $info -Raw -Encoding UTF8 | ConvertFrom-Json).AssemblyName
if (-not $assemblyName) { throw "Info.json 里没有 AssemblyName：$info" }
if ($assemblyName -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Info.json 的 AssemblyName 里有可疑字符，拒绝用它拼包内条目名: $assemblyName"
}

# **先把刚编译出来的 DLL 同步进源目录。**
# 发过一次旧 DLL：dist 里那份是几天前的构建，而脚本只按白名单打包，
# 结果 0.34.0 的包里是 0.33 的程序集。带上这一步之后不会再拿到旧货。
$builtDll = Join-Path $PSScriptRoot ("bin\Release\{0}" -f $assemblyName)
if (Test-Path -LiteralPath $builtDll) {
    $targetDll = Join-Path $SourceDir ("{0}" -f $assemblyName)
    Copy-Item -LiteralPath $builtDll -Destination $targetDll -Force
    Write-Host ("已同步编译产物: " + $builtDll)
} else {
    Write-Warning ("没找到编译产物，将打包源目录里现有的 DLL：" + $builtDll)
}

$out = Join-Path $OutDir ("{0}-v{1}.zip" -f $ModName, $Version)
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($out, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    # **白名单驱动：只打这张单子上的文件，不枚举目录。**
    # 目录里冒出来的任何东西（病毒、杂物、误放的文件）根本没机会进包。
    # **只发运行必需的文件。** README / 更新记录这类文档不进包。
    $expected = @(
        $assemblyName,
        'Info.json',
        'LICENSE',
        # 左下角打开按钮的图标。缺了它按钮还在（退化成 AES 三个字母），只是难看。
        'AesIcon.rgba'
    ) | Sort-Object -Unique

    foreach ($name in $expected) {
        $path = Join-Path $SourceDir $name
        if (-not (Test-Path -LiteralPath $path)) {
            throw ("包里必需的文件在源目录里找不到: " + $name + "（" + $path + "）")
        }
        $f = Get-Item -LiteralPath $path
        # 条目名显式用 '/' 拼。不要用 Join-Path（它是给文件系统用的，会给 '\'）。
        $entryName = $ModName + '/' + $name
        # 不用 CreateEntryFromFile —— 这个重载在 PowerShell 5.1 下第二次调用会报"流已关闭"。
        # 自己开条目、自己拷流，行为完全可控。
        $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $src = [System.IO.File]::OpenRead($f.FullName)
        try {
            $dst = $entry.Open()
            try { $src.CopyTo($dst) } finally { $dst.Dispose() }
        }
        finally { $src.Dispose() }
        Write-Host ("  + {0,-44} {1,9} bytes" -f $entryName, $f.Length)
    }
}
finally {
    $zip.Dispose()
}

# 自检一：任何一条条目名里出现 '\' 就说明打包方式又退回去了
# 自检二：**条目名必须和白名单逐条对上** —— 少一个、多一个都失败。
#         这一步是"包干净"的兜底：万一将来有人把打包改回枚举目录，
#         只要目录里多出东西就会在这里当场炸掉，而不是悄悄发给玩家。
$check = [System.IO.Compression.ZipFile]::OpenRead($out)
try {
    $bad = @($check.Entries | Where-Object { $_.FullName.Contains('\') })
    if ($bad.Count -gt 0) {
        throw ("条目名里出现反斜杠，装到 AdofaiModManager 会变成空文件夹： " + ($bad.FullName -join ', '))
    }

    $got  = @($check.Entries | ForEach-Object { $_.FullName })
    $want = @($expected | ForEach-Object { $ModName + '/' + $_ })
    $extra   = @($got  | Where-Object { $want -notcontains $_ })
    $missing = @($want | Where-Object { $got  -notcontains $_ })
    if ($extra.Count -gt 0) {
        throw ("包里出现了白名单之外的东西（可能是往目录里钻的病毒或杂物）: " + ($extra -join ', '))
    }
    if ($missing.Count -gt 0) {
        throw ("包里缺了必需的文件: " + ($missing -join ', '))
    }

    Write-Host (" 条目数 {0}，分隔符全为 '/'，且与白名单逐条一致，自检通过" -f $check.Entries.Count)
}
finally {
    $check.Dispose()
}

Write-Host ("写好: {0}  ({1} bytes)" -f $out, (Get-Item -LiteralPath $out).Length)
