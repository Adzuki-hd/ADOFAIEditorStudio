# uGUI 迁移状态（2026-10-23 02:12）

## 已完成的模块

### ① 顶栏（`UseUguiHeader = true`）

| 元素 | 状态 |
|---|---|
| 品牌名 + 金色标记 | ✅ |
| 版本号（点开更新窗口） | ✅ |
| 状态胶囊 | ✅ |
| 联机 / 帮助 | ✅ |
| 菜单（文件 / 工程 / 偏好 / 设置） | ✅ 含开发者开关、清空媒体缓存 |
| 编辑 / 只读模式切换 | ✅ 倒梯形、纯黑描边、贴顶 |

已知差异：ImGui 版是倒梯形自绘，uGUI 版用运行时生成的梯形贴图（3px 描边、顶边不画）。

### ② 右栏参数栏（`UseUguiInspector = true`）

类型覆盖已与 ImGui 版对齐：

| 类型 | 状态 |
|---|---|
| Bool / Int / Float / String / LongString / File | ✅ |
| Enum（下拉浮层：直角、纯黑、带描边） | ✅ |
| Color | ✅ |
| Vector2（并排或堆叠） | ✅ |
| **Tile**（整数字段 + 本格/起点/终点） | ✅ 本次补齐 |
| **FloatPair** | ✅ 本次补齐 |
| **Vector2Range**（X / Y 两行） | ✅ 本次补齐 |
| **MinMaxGradient**（两色 HEX + 色块） | ✅ 本次补齐，**无取色器**（只能改 HEX） |
| **ParticlePlayback**（播放/停止/重置） | ✅ 本次补齐，**缺「编辑」入口**（粒子编辑器仍是 ImGui 窗口） |

其他修正：
- 行间斑马纹已去掉（所有行透明）；
- 输入框 / 下拉框：直角、纯黑底、1px 描边；
- 滚动区尺寸跟随面板 rect（拖时间轴高度不再错位）；
- 面板自己铺整块底色（不再依赖外层 Image）；
- 打开后第 2 帧重排一次（首次进入不再"排版很小"）。

## 未完成的模块

### ③ 左栏（`UseUguiBrowser = true`，已切换）

uGUI 侧本身就是实现（ImGui 版只是把同一套 `EditorDecorationList` 重画了一遍）：
标签条、创建瓦片、计数、搜索、批量显隐/锁定、行内眼睛/锁/删除、媒体卡池全部走 uGUI。
ImGui 侧的装饰物列表、媒体面板、设置页与标签行已停止绘制。

### ④ 时间轴（`UseUguiTimelineRender / Input`,默认 false）

**这是唯一还完全依赖 ImGui 的模块**：uGUI 侧的块渲染（markerPool）、标尺、播放头在 ImGui 改造时被整段删除，目前不存在。需要新建：

1. `EditorTimelineUguiLayer`：块池（Image + TMP + 图标 + 选中描边）、标尺刻度池、播放头；
2. 三条更新路径：`RebindAll()`（数据/缩放变化）、`PanTo()`（平移）、`SetPlayhead()`（播放，只改一个 x）；
3. 交互：拖块换行、拖右边缘改时长、框选、中键平移、滚轮缩放（转发到既有方法）。

未开始的判断依据：这一块没有旧代码可复用，属于从零实现；在无法目视验证的情况下贸然切换会直接毁掉时间轴可用性，因此保持 ImGui。

### ⑤ 预览工具栏 / 弹窗 / 联机（`UseUguiChrome`,默认 false）

uGUI 侧都已建好，未切换验证。

## 运行时开关（`Settings.xml`）

```xml
<UseUguiHeader>true</UseUguiHeader>       <!-- 顶栏 -->
<UseUguiInspector>true</UseUguiInspector> <!-- 右栏参数 -->
<UseUguiBrowser>false</UseUguiBrowser>    <!-- 左栏 -->
<UseUguiChrome>false</UseUguiChrome>      <!-- 预览工具栏 / 弹窗 / 联机 -->
<UseUguiPanels>false</UseUguiPanels>      <!-- 旧的总开关，保留兼容 -->
<UseUguiTimelineRender>false</UseUguiTimelineRender>
<UseUguiTimelineInput>false</UseUguiTimelineInput>
```

任何一项出问题，改成 `false` 即回到 ImGui 绘制，不需要重新编译。

## 画布分层（已固定）

```
工作台 uGUI   30000
uGUI 浮层     32000   ← 下拉菜单、菜单拦截面
ImGui 层      31000
加载页        32767
```

ImGui 曾在 32767，导致它的全屏铺底永远盖住 uGUI 浮层——这是"下拉菜单点了没反应"的真正原因。
