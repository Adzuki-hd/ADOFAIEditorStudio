# ADOFAI Editor Studio（AES）

《冰与火之舞》的谱面编辑器工作台。UnityModManager mod，`net48` / C# 7.3；界面已迁 Dear ImGui（**布局仍由 uGUI 算**）。

- **源码（在用）**：`E:\AI工作目录\ADOFAIEditorStudio_migration`
- 旧基线快照（只作对照）：`E:\AI工作目录\ADOFAIEditorStudio` ← 两个名字只差后缀，**改错目录等于白干**，认 `Info.json` 的 `Version`
- 游戏：`D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice`
- 知识库（Obsidian）：`E:\hui\Documents\冰与火编辑器\冰与火编辑器`

## 硬约束（无条件遵守）

1. **全程中文沟通**；**注释一律用中文** —— 注释是用户学这个项目的主要材料。
2. **必须能独立发布。** 不能要求用户装 PACL2、不在运行时加载它的 dll、不调它的 API。只读它的源码作参考是允许的。
3. **不把游戏资源打进 zip。** 图标一律运行时按引用取，不拷像素。
4. **绝不 JSON 往返 `.adofai`** —— 解析再序列化会改坏数字格式（`3.201651E-06`）、属性顺序和尾逗号。只能文本层面替换，或者让游戏自己存。
5. **UI 由用户自己改。** 我只负责逻辑和 bug；UI 效果由用户自测，**不要抢焦点截图**。
6. **调试禁止猜测式修补** —— 必须先取证（加探针 / 读日志 / 读代码）再改。
7. 不要走"发现一个不对就单独硬编码一个补丁"的无边界方案。
8. 未经允许不要重做整个项目或 UI；不要执行 `git reset`、`git checkout` 这类会丢工作的操作。
9. 提交里**不留 AI 署名**（不加 `Co-Authored-By`、不留 "Generated with"）。

## 日常流程

- **改完直接部署，不必再问**：`powershell -ExecutionPolicy Bypass -File restart-and-install.ps1`
  （关游戏 → 构建 → 备份 → 安装 → 重启；日志 `install-result.log`，成功末行 `DONE gameRunning=True`）
- **动工前先备份**：`Backups/<主题>-<日期>/` 放当前源码 + 可用构建产物，并删掉最旧的一份。游戏侧 `Mods/ADOFAIEditorStudio/Backups/` 要定期按"每天留一个"清。
- **分发包**放 `dist/`，并复制一份到桌面。
- **收口时主动做**：① 记知识库；② 备份轮换；③ 分发包。
- 构建验收标准是 **0 错误**；警告 40~50 条是清理到一半的孤儿字段，**不是**验收标准。

## 技能路由（用它，别等用户点名）

| 任务 | 技能 |
|---|---|
| 动知识库 | `obsidian-cli`（读 / 追加 / 校验断链；**别用 bash 翻文件**） |
| ADOFAI 内部、PACL2、反编译、打包发布、版本绕过 | `adofai-modding` |
| 联机做谱协议 | `adofai-mp-chart` |
| UI / 设计审查 | `design-doctrine`、`design-review`、`refactoring-ui-skill` |
| 查 bug 根因 | `investigate` |
| 要独立第二意见 | `codex` |
| 发版 / 变更日志 / 文档 | `ship`、`changelog-generator`、`document-release` |
| 软件著作权材料 | `softcopyright` |

用户不熟技能名，**默认不该让他记这些** —— 该由我按任务匹配。

## 关键文档在哪

- 知识库首页 `00-知识库首页.md`；技术 / 决策 / 记录都在那儿。
- **界面怎么画的**：`03-技术/ImGui 迁移架构.md`（布局权留在 uGUI、一个面板怎么接进来、纹理/UV/输入/字体那些坑）
- **时间轴**：`05-功能/多轨时间轴.md`（自由画布、按时间装箱、撤销语义）
- **已知坑与待办**：`08-记录/问题清单.md`
- **回归清单**：`04-开发/测试清单.md`
- 写代码前建议先看 `03-技术/技术架构.md` 里"状态边界"那一节（写入只有一条路等硬规矩）。
