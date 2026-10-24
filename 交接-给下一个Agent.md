# 给下一个 AI Agent 的任务提示词

> 直接把下面「提示词正文」整段复制给它。这是自包含的，不依赖任何历史对话。

---

## 提示词正文

你接手 ADOFAI Editor Studio（冰与火之舞编辑器 mod）的 **UI 从 ImGui 迁移到 uGUI** 工作。前一个 agent 已做过一轮，踩了很多坑，下面是它用血的教训换来的约束。**先读完再动手**。

### 0. 你的任务（一句话）

按模块把工作台从 ImGui 绘制改成 uGUI 绘制，**每个模块都要按现有 ImGui 实现重写一份新的 uGUI 代码**，不允许复用仓库里遗留的旧 uGUI 骨架。

### 1. 环境

| 项 | 值 |
|---|---|
| 源码 | `C:\Users\hui\Desktop\ADOFAIEditorStudio-tempdev` |
| 构建 | `dotnet build ADOFAIEditorStudio.csproj -c Release`（**必须 0 错误**） |
| 部署 | `powershell -ExecutionPolicy Bypass -File .\restart-and-install.ps1`（会重启游戏，末尾打印 `DONE gameRunning=True`） |
| 运行时设置 | `D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIEditorStudio\Settings.xml` |
| 游戏日志 | `%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log` |
| 技术栈 | Unity + Harmony mod，C# 7.3（**不能用 `??`、`?.`、`?:` 混类型、不能用 target-typed new**），ImGui.NET，TMP |

改完必须：构建 0 错误 → 部署 → 读日志确认无 `Exception`。日志里 `grep -c 'ADOFAIEditorStudio.*Exception'` 必须是 0。

### 2. 最重要的一条铁律

**ImGui 版是功能规格，uGUI 版是要被重写掉的旧代码。**

仓库里有两套 UI 代码：

- `EditorImGui*.cs`（约 9600 行）= **当前真值**。用户要的每个按钮、每行文字、每个交互都在这里。
- `EditorStudioOverlay.Build*` + `EditorDecorationList` + `EditorPropertyPanel` + `EditorTimelineView` = **ImGui 改造之前的旧 uGUI 骨架**，功能冻结在几年前，缺很多后来加的东西。

正确做法（每一步都要照做）：

1. 读对应的 `EditorImGui*` 实现，列出它画了什么（结构清单给用户确认）；
2. **新建一个文件**按那份清单重写 uGUI（例：已完成的 `UiPreviewChrome.cs`）；
3. 把旧骨架里对应的那段**删除**（不是保留加开关）；
4. 打开该模块的开关，构建 + 部署 + 读日志；
5. 让用户目视确认，逐条对齐。

错误做法（前一个 agent 反复犯，被用户多次批评）：直接打开旧 uGUI 代码 + 打补丁。结果就是"功能变少、样式回到旧版"。

### 3. 当前状态（2026-10-23 09:56）

开关在 `Main.cs` 与 `Settings.xml` 各存一份，**两处都要改**：

| 开关 | 当前 | 含义 |
|---|---|---|
| `UseUguiPreview` | **true** | 预览工具栏外壳（唯一已完成重写的模块） |
| `UseUguiHeader` | false | 顶栏 + 菜单（旧骨架，未重写） |
| `UseUguiInspector` | false | 右栏参数栏（旧骨架，未重写） |
| `UseUguiBrowser` | false | 左栏装饰物/媒体（旧骨架，未重写） |
| `UseUguiChrome` | false | 预览工具栏/弹窗/联机的总开关（已拆细，一般不用） |
| `UseUguiTimelineRender` | false | 时间轴渲染（**完全没有 uGUI 实现**） |
| `UseUguiTimelineInput` | false | 时间轴交互（同上） |
| `UseUguiPanels` | false | 历史遗留总开关，保留兼容 |

构建通过、日志无异常。除预览外壳外，所有面板目前由 ImGui 绘制。

### 4. 已完成（可参考的样板）

`UiPreviewChrome.cs` —— 唯一按新规则写的文件：只建「强调竖条 + 预览标题 + 状态读数」三件，状态文本变了才写；旧的 `HeadingBar / Heading / HeadingRule / BuildLaneFilterButtons` 已从 `BuildPreview` 里删除。**照这个文件的写法做其它模块。**

### 5. 未完成（按建议顺序）

1. **左栏**（装饰物列表 / 媒体页 / 设置页）—— 规格在 `EditorImGuiDecorationList.cs`（456 行）与 `EditorImGuiMediaPanel.cs`。头部三行：创建瓦片（图片/文本/粒子/对象，32×32 图标）、计数、搜索框（下划线样式）、全部隐藏/全部锁定。行内：图标、名字、`pN sHits` 标签、眼睛/锁/删除（悬停才露）。
2. **右栏参数栏** —— 规格在 `EditorImGuiInspector.cs`（2200 行）。类型必须覆盖：bool/int/float/string/长文本/file/enum/color/vector2/tile/floatPair/vector2Range/minMaxGradient/particlePlayback。旧 `EditorPropertyPanel` 已补过一轮类型（可借用其数据读写 API，但**布局与外观要按 ImGui 版**）。
3. **时间轴** —— 最大一块，uGUI 渲染在 ImGui 改造时被整段删除，需要从零建：块池（Image + TMP + 图标 + 选中描边）、标尺刻度池、播放头；三条更新路径 `RebindAll / PanTo / SetPlayhead`；交互（拖块换行、拖右边缘改时长、框选、中键平移、滚轮缩放）转发给 `EditorTimelineView` 已有方法。
4. **顶栏 + 菜单** —— 规格 `EditorImGuiHeader.cs` / `EditorImGuiMenuBar.cs`。注意顶栏正中是「编辑/只读」倒梯形切换（纯黑描边、贴顶、无顶边）。

### 6. 用户的 UI 审美（硬规则，违反会被打回）

1. **整体只出现三种颜色**：背景黑（`#0B0B0B`）、文字白（次要信息用白的透明度）、强调天蓝（`#0EA5E9`）。
2. **拒绝圆角**：任何控件都不要圆角。
3. **控件默认无底色**，只有 hover 才浮出白 8% 的淡底。
4. **选中态用「强调色文字 + 2px 下划线」**，不要色块/胶囊。
5. 风格基准 = 上一版 ImGui 界面（纯平、无底、细线分隔）。
6. 模块之间不要"底色块"分界，整屏読み成一层。

### 7. 已知坑（每条都是实际踩过的，照着避开）

1. **`SuppressXxx(false)` 的语义是"显示"，不是"不动"**。无条件调一遍会把面板强行打开盖在别的东西上。
2. **画布分层**：uGUI 工作台 30000、ImGui 层 31000、uGUI 浮层 32000、加载页 32767。**ImGui 层不能占 32767**，否则它的全屏铺底永远盖住 uGUI 下拉菜单（"菜单点了没反应"的真正原因）。
3. **`DrawModuleBlocks` 会给未迁移模块铺底**，画在 31000 上。已迁移的模块必须在里面跳过，否则被整块盖住。
4. **`ApplyWorkspaceLayout()` 会覆盖建库时设定的坐标**。改布局常量的同时必须改这个函数，否则"改了间距没变化"。
5. **布局统一基准**：模块顶/底用 `BlockRowTop` / `BlockRowBottom`，不要再用 `PreviewPanelY`（差 8px，三块上沿会不齐）。
6. **参数栏的内层容器尺寸**按外壳 rect 算（`panelRect.rect.size`），建库那一刻外壳还没排版完，读到 0 就会"首次进入排版很小，拖一下才变大"。
7. **预览不是纹理，是"透着看游戏的窗"**：铺任何不透明全屏底都会把画面挡住；挡住游戏原生编辑器界面靠的是 `ViewportMatte` 四条不透明遮沿，且它们必须在 `ApplyWorkspaceLayout` 里跟着舞台矩形重算，否则拖时间轴高度会盖住画面或露出原生 UI（露出就是满屏"重影"）。
8. **`Settings.xml` 不要用 PowerShell 的 `-replace` 插行**（转义会写成字面量 `` `r`n ``，把 XML 写坏）。用 Python 读写。
9. **TMP 系统字体**：`Font.CreateDynamicFontFromOSFont` 建出的 Font 没有字体文件引用，`CreateFontAsset(Font, …)` 返回 null；要用「家族名 + "Regular" + 字号」那个重载（现有 `TryCreateSystemFont()` 已实现，走反射调用）。
10. **ImGui 侧字体**已是系统字体（`C:\Windows\Fonts\msyh.ttc`），不要换成游戏字体。
11. **行间斑马纹**已删除，不要在参数栏行里再加交替底色。
12. 面板自铺底色的做法（`EnsureBackdrop` / `BrowserFill`）只在需要时用；当前 `ModuleFill` 已改回透明，靠遮沿+单层底。

### 8. 验收方法

- 构建 `0 个错误`；
- 部署后日志 `ADOFAIEditorStudio.*Exception` 计数为 0；
- 视觉：让用户确认（**不要代替用户点 GUI**，给清单让他自己点）；
- 可以用 Python + Pillow 对用户截图**采样像素**来判定颜色是否一致（比目测可靠，前一轮多次靠它定位）。示例：
  ```python
  from PIL import Image
  im = Image.open(path).convert("RGB")
  print(im.getpixel((x, y)))
  ```

### 9. 用户偏好（沟通层）

- 结论先行，讨厌啰嗦，能一句说完别写三段；
- 不确定就去查/去实测，不接受"应该是"；
- 需要一个操作时给他**清单自己点**，不要截图模拟点击；
- 改配置前备份到带时间戳的目录；
- **不要再写交接回执 md**（除了这一份是他明确要的）。

### 10. 第一步建议

先给用户一份「左栏」的结构清单（对照 `EditorImGuiDecorationList.cs` 逐条列出：创建瓦片、计数、搜索、批量按钮、行内三按钮、悬停行为），确认后再建 `UiBrowserPane.cs` 重写。不要一次改多个模块。
