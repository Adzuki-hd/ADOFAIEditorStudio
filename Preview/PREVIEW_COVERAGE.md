# 预览覆盖矩阵

当前后端：`EvaluatorPreviewBackend`

当前已接入 13 个视觉域。`FilterAdvanced` 已加入基础原版字段求值；粒子、视频和特殊装饰刷新仍单独标记为计划中。

时间轴拖动只改变绝对目标时间；每次刷新从默认状态和已分析事件求值，直接写入 ADOFAI 原版 Unity 组件。松手保持最后一次求值结果，不调用 `scnEditor.Play()` 重新跳转。

## 当前已接入

| 域 | 状态 | 说明 |
|---|---|---|
| Planet | 已接入 | 普通双球楼层直接求值；复杂楼层使用原生楼层检查点回退 |
| Camera | 已接入 | MoveCamera 位置、旋转、缩放、Ease |
| Flash | 已接入 | 前景/背景闪光 |
| Bloom | 已接入 | VideoBloom 参数和 Ease |
| CustomBackground | 已接入 | 背景颜色和图片状态 |
| ScreenTile | 已接入 | 屏幕分块和 Ease |
| ScreenScroll | 已接入 | 屏幕滚动偏移 |
| HallOfMirrors | 已接入 | 原版控制器开关 |
| RecolorTrack | 已接入 | FloorRenderer 颜色、样式、脉冲的绝对时间求值 |
| MoveTrack | 已接入 | 地板位置、旋转、缩放、不透明度的绝对时间求值 |
| Filter | 已接入 | 常用 SetFilter 组件和强度映射 |
| FilterAdvanced | 已接入 | 原生前景/背景滤镜组件、float/int/Color/Vector2 字段、Ease 和 disableOthers |

## 下一批

| 域 | 状态 | 主要难点 |
|---|---|---|
| DecorationCore | 已接入 | 装饰物默认状态、MoveDecorations 绝对求值 |
| DecorationImage/Text/Object | 计划中 | 原版材质、Mask、文字和对象刷新顺序 |
| Particle | 计划中 | 粒子历史状态、Emit/Play 与随机种子 |
| Video | 计划中 | VideoPlayer 帧定位和循环状态 |

## 验收口径

“已接入”不等于所有谱面和所有效果 100% 像原版。只有在同一游戏版本、同一谱面、同一分辨率下做逐帧截图差分后，才能把某个域标为 `Native Visual Match`。粒子随机状态、视频解码、第三方 Mod 仍需单独验证。
