# FrameCacheSpike

这是 ADOFAI Editor Studio 的隔离原生渲染实验，不属于主 Mod 运行路径。

## 这次验证了什么

- Worker 通过 stdio JSON Lines 独立进程启动、握手、加载渲染身份、渲染时间窗和关闭。
- Host 端以 game build、Mod 集合、谱面快照、视觉设置、分辨率和 seed 组成渲染身份，缓存不会跨身份复用。
- Worker 产生 10 秒、30 fps 的确定性帧事件，Host 端保存 LRU 原生帧缓存。
- 时间线前拖、后拖和来回拖只读取已缓存的原生帧。
- 缓存命中使用半帧容差，任意拖动时间会选择最近的缓存帧，而不是只命中精确时间戳。
- 未命中时严格模式保持最近一张原生帧并报告 WaitingForNative，不伪造近似帧。
- 换谱面或环境身份会清除当前显示帧；不同 session 的帧会被拒绝。
- 松手后若目标帧尚未返回，会保持等待并在迟到的原生帧到达后定格为 HeldCachedNative，不会退出游玩、重新选轨道、调用 scnEditor.Play 或触发读秒。
- Worker 支持异步 render_window、cancel 和 Host 侧读取超时，过期窗口不会被当成完成。

## 运行

原型控制面当前使用 **stdio JSON Lines**，便于在不启动游戏的条件下测试握手、取消和超时；生产设计目标仍是把控制面替换为命名管道。MMF/分段文件只承载帧数据与描述信息，两者不要混同。

    dotnet run --project .\FrameCacheSpike\FrameCacheSpike.csproj -- --self-test
    dotnet run --project .\FrameCacheSpike\FrameCacheSpike.csproj -- --benchmark

`--benchmark` 默认以 1920×1080、RGBA32、30 fps 测量 300 个 MMF 样本和 90 个磁盘帧。MMF 使用八槽、双进程 producer/consumer，分成无节流 burst 与按目标帧率 paced 两组，分别统计 producer 开始写入到复制完成、正式发布到复制完成的 P50/P95/P99、信号量真实等待、撕裂、丢帧与子进程资源。磁盘路径使用 32 帧一个 `.tmp` 分段，完成并 flush 后原子发布成 `.raw`，统计 buffered 写入、包含 durable flush 的有效吞吐和 warm-cache 跨段随机整帧读取。

每个合成帧的每个 4 KiB 页都写入帧号和页号；随机计时后还会顺序重读全部帧。自测会故意破坏中间页签名，并验证奇数/不一致 MMF 序列被拒绝。可用 `--ring-samples`、`--frames`、`--ring-slots`、`--segment-frames`、`--random-reads`、`--temp-root` 和 `--no-durable-flush` 调整。单帧、MMF 环、Host 帧池和总磁盘集分别有硬上限；每次运行只删除带所有权标记的 `run-<guid>` 子目录，不删除用户指定的根目录。

该基准只回答合成 CPU 字节缓冲与本机缓存介质的预算。它包含两个普通 .NET 进程的 MMF 并发，但不包含 Unity 最终屏幕捕获、GPU readback、两个 ADOFAI/Unity 进程并发、Texture2D 上传或真实画面编码。因此结果只能是 `PARTIAL_ONLY`，不能作为原画 Go 结论。stdio JSON Lines 继续只承载控制消息和帧描述符，真实像素不得 Base64 塞进 JSON。

当前 Worker 的 native-frame:// 载荷是协议占位符，不是游戏截图。它故意没有宣称原画已经完成。下一阶段必须把 WorkerMain.RenderWindow 后面的适配器替换成独立 ADOFAI 进程中的真实 RenderTexture/帧文件捕获，并用同一游戏版本、Mod 清单和谱面快照做逐帧差分。

协议状态机当前仍只保存 token 和帧元数据；独立传输 smoke test 会分配小型 RGBA 缓冲，完整基准可测 1080p 合成 RGBA32。真实 GPU 捕获延迟、真实画面压缩率与像素一致性仍未验证。

## 不在本实验范围内

- 不修改 ADOFAIEditorStudio.dll 主工程。
- 不安装 DLL，不启动第二个游戏进程。
- 不把静态求值器标记为原画。
- 不尝试让原版 Tween、粒子、VFX 或视频可靠逆放；反向拖动依赖已缓存帧，未命中就等待正向 Worker 渲染。
