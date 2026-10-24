# ADOFAIEditorStudio — 项目长期记忆

## 发布链路（2026-10-21 核实）

| 环节 | 位置 | 说明 |
|---|---|---|
| 代码仓库 | `Adzuki-hd/ADOFAIEditorStudio`（**PRIVATE**） | 本地 `origin`，`main` 分支 |
| 分发清单 | `Adzuki-hd/ADOFAIEditorStudio-manifest`（public） | 根目录放 `ADOFAIEditorStudio-v<版本>.zip` + `version.json` |
| 更新检查 | mod 读 `version.json` 的 `latest` / `url` | `url` 走 jsDelivr：`https://gcore.jsdelivr.net/gh/Adzuki-hd/ADOFAIEditorStudio-manifest@main/<zip>` |

发布四步：
1. 改 `Info.json` 的 `Version`
2. `powershell -ExecutionPolicy Bypass -File pack-release.ps1 -Version X.Y.Z`
   → 产出 `dist/ADOFAIEditorStudio-vX.Y.Z.zip`（白名单打包，少一个文件就失败）
3. 把 zip 提交到 manifest 仓库根目录
4. 更新 manifest 的 `version.json`：`min` / `latest` / `url` / `note`（`note` 是给玩家看的中文更新说明）

## 工具链认证状态

- `gh` CLI：已登录 `Adzuki-hd`，scopes = `gist, read:org, repo, workflow` → 可访问私库、可建 release / 上传资源
- 本地 git：`git ls-remote origin` 通（凭证在系统凭据管理器）
- MCP GitHub connector：账号 `Adzuki-hd`，**token 看不到私库**（`/repos/ADFAIEditorStudio` 返回 404），
  只能操作 public 的 manifest 仓库 → 私库操作一律走 `gh` / `git`，不要用 MCP 试

## 构建与部署（开发期）

- 构建：`dotnet build ADOFAIEditorStudio.csproj -c Release`
- 部署到游戏：`powershell -ExecutionPolicy Bypass -File .\restart-and-install.ps1`（会重启游戏，末尾打印 `DONE gameRunning=True`）
- 回滚：`rollback-to-baseline.ps1`；安装脚本自动备份到 `Mods/ADOFAIEditorStudio/Backups/`

## 两个工作副本（易踩）

| 路径 | 用途 | 状态 |
|---|---|---|
| `E:\AI工作目录\ADOFAIEditorStudio_migration` | **真正的源仓库**，玩家装的版本来自这里 | 2026-10-21 已推 `34d6067`（0.33.2） |
| `C:\Users\hui\Desktop\ADOFAIEditorStudio-tempdev` | 本次会话的临时开发副本 | 2026-10-21 16:18 已 ff 到 `34d6067` |

**发布前必须先看版本号落在哪一棵树上**：10-21 那次 `Info.json` 在 tempdev 是 0.33.1、
在 migration 已经是 0.33.2（多出 1 个提交），差点发了个旧版。

## 发布时补一步：清 jsDelivr 缓存

`version.json` 走 jsDelivr 会缓存，**推完必须清**，否则老玩家查不到新版：

```
curl -sL "https://purge.jsdelivr.net/gh/Adzuki-hd/ADOFAIEditorStudio-manifest@main/version.json"
```

清完逐个端点确认：`api.github.com/.../contents/version.json`（mod 第一顺位）、
`cdn.jsdelivr.net`、`fastly.jsdelivr.net`。fastly 有时慢一拍，会在 TTL 内自愈。

## manifest 仓库侧栏的「Claude」贡献者（2026-10-21 核查）

`Adzuki-hd/ADOFAIEditorStudio-manifest` 概览页侧栏 Contributors 显示 2 人
（Adzuki-hd + `claude`），但 GitHub 侧全部数据都指向 1 人：

- `commits`（87 条）全部归属 `Adzuki-hd`；`commits?author=claude` = 0
- `contributors`（含 `anon=1`）= 1；`collaborators` = 1；`invitations` = 空
- 全量扫描提交对象：无 `Co-authored-by`、无 `anthropic`
- 本地 clone 全部 refs 无其他作者

结论：这是 GitHub 侧栏的**缓存条目**（历史里曾有 claude 的提交、后来被改写/删除），
不是可编辑的对象 —— 计数是推导值，不能手动移除。处置：等缓存重算或找 GitHub Support。

## 协作约定（2026-10-21 起）

- **不再写交接回执 md**：用户明确「这个项目不用交接了，你全权负责」。后续直接改、直接发版，
  过程记录只写 `.workbuddy/memory/`。

## 装饰物/遮罩域的两条硬规则

1. **`opacity` 不能用来剔除遮罩件**：遮罩失效只有三条路 —— `visible` 关、缩放归零、
   移出遮罩深度范围。按不透明度跳过 transform 写入会把遮罩冻在某一帧的大小上，
   表现是「遮罩关不掉 / 该消失的一直在裁」。粒子同理（已在 727 格那轮踩过）。
2. **AO 的遮罩裁剪不靠 Unity 的 `SpriteMask`**：走 `SpriteAlphaMaskUtils` 按 tag 建索引
   （`taggedAlphaMaskCache[mask.targetTag]`）→ OBB 重叠判定 → 合成 RT。
   必须主动调 `SetMaskingTarget(targetTag)` 和 `ForceUpdateAlphaMaskCache(deco)`，
   否则缓存恒空，裁剪等于不存在。

## UI 风格硬规则（用户 2026-10-23 明确）

1. **整体只出现三种颜色**：背景（黑）、文字（白，次要信息用白的透明度）、强调（天蓝 Accent）。
2. **拒绝圆角**：`CornerLarge` / `CornerSmall` 一律 0，任何新控件都不要给圆角贴图。
3. 风格基准 = 上一版 ImGui 界面（纯平、无底、细线分隔），uGUI 迁移要向它看齐，不要另起配色。
4. 控件默认**无底色**，只有 hover 才浮出白 8% 的淡底。
5. 选中态用「强调色文字 + 2px 下划线」，**不要色块/胶囊**。

注：`UiTheme` 里那轮「纯平背景」把 SurfacePanel 也设成透明，是给 ImGui 铺底用的；
迁回 uGUI 后面板需要 #0B0B0B 这种极暗的填充，否则整块工作台是空的。
