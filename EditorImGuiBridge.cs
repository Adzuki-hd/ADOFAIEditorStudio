using System;
using System.IO;
using System.Runtime.InteropServices;
using ADOFAIEditorStudio.Ui;
using ImGuiNET;
using UnityEngine;
using UnityEngine.UI;
// ImGui 自己的顶点/尺寸类型用的是 System.Numerics，而 Unity 也有一套同名的
// Vector2/Vector4。两个 using 一起来会撞名，所以给 ImGui 那套起个别名。
// 记法：NVec2 = 给 ImGui 用的，Vector2 = 给 Unity 用的。
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// ImGui 与 Unity 之间的桥。
    ///
    /// **架构：ImGui 只管"摆哪儿、什么颜色"，真正的绘制交给 Unity 的 Canvas。**
    /// ImGui 每帧产出一堆顶点（`ImDrawData`），这里把它们读出来、换算成 Unity
    /// 画布的坐标、塞进 Mesh，再挂到 CanvasRenderer 上。所以：
    ///
    ///   · 不需要任何图形后端 —— cimgui.dll 用原版即可（不带 DX11/OpenGL 那些）
    ///   · 不需要钩 D3D11、不开额外窗口、不改原生库
    ///   · 输出的是 Unity 的对象，所以排序、遮罩、射线都还是 Unity 那一套
    ///
    /// 这套做法参照的是 CheryTools 的实现（MIT），它的坐标为：
    /// ImGui 左上原点 → Unity ScreenSpaceOverlay 画布的中心原点，
    /// 也就是 x 减半屏宽、y 取半屏高减自己。
    ///
    /// **现在承载的界面：说明窗口。** 现有的 uGUI 工作台完全不受影响 ——
    /// 迁移是共存的、一个面板一个面板搬，搬完一个才轮到下一个。
    /// </summary>
    internal sealed class EditorImGuiBridge : MonoBehaviour
    {
        /// <summary>
        /// 当前这个桥。工作台那边要通过它开关说明窗口（它建在和 uGUI 同一个
        /// 宿主物体上，但两边互不认识，所以留一个静态入口）。
        /// </summary>
        internal static EditorImGuiBridge Instance { get; private set; }

        // ---------------------------------------------------------------- 原生库

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        /// <summary>
        /// 先把 cimgui.dll 显式加载进来。
        ///
        /// ImGui.NET 是靠 P/Invoke 找 `cimgui` 的，默认只搜程序目录和系统路径。
        /// 先自己 LoadLibrary 一遍，句柄就在进程里了，后面的 P/Invoke 能直接复用
        /// —— 省得去改 PATH 或者把库丢到系统目录。
        /// </summary>
        private static bool PreloadNative(string directory)
        {
            string path = Path.Combine(directory ?? string.Empty, "cimgui.dll");
            if (!File.Exists(path))
            {
                Main.Logger.Error("[ImGui] 找不到 cimgui.dll：" + path);
                return false;
            }
            IntPtr handle = LoadLibrary(path);
            if (handle == IntPtr.Zero)
            {
                Main.Logger.Error("[ImGui] 加载 cimgui.dll 失败，Win32Error="
                    + Marshal.GetLastWin32Error());
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 状态

        private IntPtr _context = IntPtr.Zero;
        /// <summary>ImGui 这层的排序号：short 的上限，不能再高。</summary>
        private const int OverlaySortingOrder = 32767;

        private Canvas _canvas;
        private Material _material;
        private Texture2D _fontTexture;
        private Image _blocker;
        /// <summary>
        /// 常驻面板那块"挡点击"的遮罩：**不压暗**（全透明），只是把那块地方接住，
        /// 免得点面板穿到后面的工作台上。范围跟着面板走，每帧同步。
        /// </summary>
        private Image _panelBlocker;
        /// <summary>
        /// 左栏装饰物列表的挡点击层。
        ///
        /// **左右各一块，不能并成一个大矩形** —— 左栏在预览左边、配件栏在右边，
        /// 两个一并就正好把中间的预览区整个罩住（那块是要能点的）。
        /// </summary>
        private Image _decoBlocker;
        /// <summary>媒体页那块的挡点击层。和装饰物列表同一块坑，两者只会有一个在。</summary>
        private Image _mediaBlocker;
        /// <summary>左栏设置页那块的挡点击层。和三页里的另外两块同一块坑。</summary>
        private Image _settingsBlocker;
        /// <summary>顶栏那条的挡点击层 —— 藏掉 uGUI 那条之后由它接住点击。</summary>
        private Image _headerBlocker;
        /// <summary>预览工具栏那一条的挡点击层。**只盖工具栏** —— 舞台那块要能点。</summary>
        private Image _previewBlocker;
        /// <summary>时间轴标题行那一条的挡点击层。</summary>
        private Image _timelineHeaderBlocker;
        /// <summary>
        /// 事件面板那排的挡点击层。
        ///
        /// **必须单独有一份**：那排按钮原来是 uGUI，点击由按钮自己吃掉；改成
        /// ImGui 画之后，点在按钮上的那一下会**穿到下面的游戏编辑器**去
        /// （顺手选中了格子、或者触发了原版自己的东西）。
        /// </summary>
        private Image _eventPaletteBlocker;
        /// <summary>时间轴内容区的挡点击层 —— 块归 ImGui 管了，得把 uGUI 那条路挡掉。</summary>
        private Image _timelineBodyBlocker;
        /// <summary>悬浮球的点击遮挡块（工作台关着时也要生效）。</summary>
        private Image _ballBlocker;
        /// <summary>粒子编辑器窗口的点击遮挡块。</summary>
        private Image _particleBlocker;
        /// <summary>顶栏（ImGui 版）。常驻，永远在工作台最上面那一条。</summary>
        private EditorImGuiHeader _header;
        private EditorImGuiMenuBar _menuBar;
        /// <summary>预览面板的页眉那一条（ImGui 版）。只接管工具栏，不碰舞台。</summary>
        private EditorImGuiPreviewPanel _previewPanel;
        /// <summary>时间轴标题行（ImGui 版）。只接管最上面那 42 像素。</summary>
        private EditorImGuiTimelineHeader _timelineHeader;
        private EditorImGuiEventPalette _eventPalette;
        /// <summary>自己复刻的粒子编辑器（原版那扇被工作台画布压住，看不见）。</summary>
        internal readonly EditorImGuiParticleEditor ParticleEditor = new EditorImGuiParticleEditor();
        /// <summary>时间轴内容区（ImGui 版）。目前只有标尺。</summary>
        private EditorImGuiTimelineBody _timelineBody;
        /// <summary>参数栏（ImGui 版）。常驻面板，不是模态窗口。</summary>
        private EditorImGuiInspector _inspector;
        /// <summary>
        /// 左栏设置页（ImGui 版）。**和参数栏是同一个类**，只是目标、位置、标题不同
        /// （见 EditorImGuiInspector 的 _settingsPane）。
        /// </summary>
        private EditorImGuiInspector _settingsInspector;
        /// <summary>装饰物列表（ImGui 版）。</summary>
        private EditorImGuiDecorationList _decorationList;
        /// <summary>媒体页（ImGui 版）。和装饰物列表同一个坑，同一时刻只显示一个。</summary>
        private EditorImGuiMediaPanel _mediaPanel;
        private bool _helpOpen;
        private bool _versionOpen;

        /// <summary>
        /// 「创建工程」那一屏开着没有。
        ///
        /// 它和别的窗口不一样：**不是随时能开的**，只在「切到编辑模式、而这张谱
        /// 还没有工程」那一下被工作台推开，答完就关。
        /// </summary>
        private bool _projectCreateOpen;

        /// <summary>工程名输入框的内容。开窗时由工作台填上默认名。</summary>
        private string _projectName = "";

        /// <summary>会建到哪儿 —— 只读展示，让用户知道东西放哪。</summary>
        private string _projectPath = "";

        /// <summary>用户按了「建工程」。工作台取走之后自己动手建。</summary>
        private bool _projectCreateConfirmed;

        /// <summary>用户按了「取消」或者把窗口关了。工作台取走之后退回只读。</summary>
        private bool _projectCreateCancelled;

        /// <summary>「布局还没存」那扇窗开着没有。</summary>
        private bool _confirmSaveOpen;

        /// <summary>
        /// 用户在那扇窗上的答复。0 = 还没答，1 = 保存，2 = 不保存，3 = 取消。
        /// 用数字而不是三个布尔：它们是互斥的，三个布尔能凑出"同时选了保存和取消"
        /// 这种不存在的状态。
        /// </summary>
        private int _confirmSaveAnswer;
        /// <summary>
        /// 版本说明窗口被**用户自己**关掉了（× 或 Esc），等着工作台来收这个信号。
        ///
        /// 用"取一次就清"的标记而不是回调：工作台那边每帧问一句就够了，
        /// 不必让桥接反过来依赖工作台。
        /// </summary>
        private bool _versionClosed;
        /// <summary>模组自己的目录。随包带的资源（cimgui.dll、UiFont.ttf）都在这儿。</summary>
        private string _assemblyDirectory;
        /// <summary>点了加群链接。**只记标记、帧画完了再动手**（见 DrawRows）。</summary>
        private bool _qqLinkClicked;

        private readonly System.Collections.Generic.List<CanvasRenderer> _renderers
            = new System.Collections.Generic.List<CanvasRenderer>();
        private readonly System.Collections.Generic.List<Mesh> _meshes
            = new System.Collections.Generic.List<Mesh>();

        /// <summary>
        /// `ImDrawCmd.TextureId` → Unity 贴图。
        ///
        /// 我们不接 ImGui 的原生渲染后端，所以"纹理 ID"由我们自己定：约定用
        /// Unity 贴图的 `GetInstanceID()`（字体图集就是这么登记的）。画的时候
        /// 按每条 cmd 记的 ID 去这张表里翻，翻不到就退回字体图集 —— 至少画出
        /// 字来，不会给出一片空白。
        ///
        /// **里面存的贴图有游戏自己的，绝不能 Destroy。** 这里只清表，不碰贴图。
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<IntPtr, Texture> _textures
            = new System.Collections.Generic.Dictionary<IntPtr, Texture>();

        private Vector3[] _vertices = new Vector3[0];
        private Vector2[] _uvs = new Vector2[0];
        private Color32[] _colors = new Color32[0];
        private int[] _indices = new int[0];
        private int[] _commandIndices = new int[0];

        private float _scale = 1f;
        private bool _ready;
        private ImFontPtr _uiFont;
        /// <summary>小字号那一档。没建出来时是空指针，用之前先看 HasSmallFont。</summary>
        private ImFontPtr _smallFont;
        /// <summary>
        /// 界面实际用到的字，拼成 ImGui 的字形范围表。
        ///
        /// **文案里出现的字符必须都在这儿** —— 范围表之外的字符 ImGui 画不出来
        /// （会退回成问号或空白）。这里直接从说明窗口的正文拼出来，就是为了
        /// 让人改文案时不可能漏掉：改了正文，范围跟着变。
        ///
        /// **故意写成方法、不写成静态字段**：静态字段是按声明顺序初始化的，
        /// 只要这个字段排到 `HelpLines` 前面，轮到它时 `HelpLines` 还是 null，
        /// `string.Join` 当场抛 ArgumentNullException —— 崩的是**整个类的类型
        /// 初始化**，表现是"按了没反应"，日志里只有一句类型初始化失败。
        /// 写成方法就不存在这个先后顺序问题。
        /// </summary>
        /// <summary>
        /// 界面实际用到的字，拼成 ImGui 的字形范围表。
        ///
        /// **三部分，缺一不可：**
        ///   1. 我们自己的文案（说明窗口、版本说明窗口……）
        ///   2. 通用规范汉字表一级 3500 字 —— 参数栏要显示游戏自己的事件名和参数名
        ///   3. 可打印 ASCII + 中文标点 + 全角 + 常用符号
        ///
        /// 第 3 条是**漏过一次才补的**。一开始只收"我们自己文案里出现过的字符"，
        /// 于是游戏给的动态字符串里只要有一个我们没写过的字符，屏幕上就是一个问号 ——
        /// 实测中招的是单位 "Beats" 的 B、未设偏移算出来的 "NaN" 的 N，还有 "%" 和 "°"。
        /// 动态字符串的字符集推不出来，只能整段收。
        ///
        /// 这三部分和 `tools/make-font-subset.py` 收的是**同一份**（第 2、3 条直接由
        /// 那个脚本生成到 <see cref="UiGlyphTable"/>），所以不会出现"图集里有、字体里没有"。
        /// </summary>
        private static ushort[] BuildUiGlyphRange()
        {
            string text = string.Join(string.Empty, HelpLines)
                + string.Join(string.Empty, VersionLines)
                + UiGlyphTable.Common
                + UiGlyphTable.Symbols
                + HelpTitle + VersionTitle + HelpFooter;
            return BuildRange(text);
        }

        /// <summary>
        /// 把一串文字里出现的字符编成 ImGui 的范围数组。
        ///
        /// 格式是"起-止"成对、末尾一个 0 —— 连续的码点会合并成一段，
        /// 所以几百个字最后通常只剩几十段。
        /// </summary>
        private static ushort[] BuildRange(string text)
        {
            System.Collections.Generic.SortedSet<ushort> codes
                = new System.Collections.Generic.SortedSet<ushort>();
            for (int i = 0; i < text.Length; i++)
            {
                codes.Add(text[i]);
            }

            System.Collections.Generic.List<ushort> range
                = new System.Collections.Generic.List<ushort>();
            bool first = true;
            ushort start = 0;
            ushort prev = 0;
            foreach (ushort code in codes)
            {
                if (first)
                {
                    start = code;
                    prev = code;
                    first = false;
                    continue;
                }
                if (code == prev + 1)
                {
                    prev = code;
                    continue;
                }
                range.Add(start);
                range.Add(prev);
                start = code;
                prev = code;
            }
            if (!first)
            {
                range.Add(start);
                range.Add(prev);
            }
            range.Add(0);
            return range.ToArray();
        }

        /// <summary>
        /// 把范围表拷进非托管内存，返回一个地址固定不动的指针。
        ///
        /// **不能直接 `fixed`**：`fixed` 只在那个代码块里钉住数组，而 ImGui
        /// 要到建图集时才去读它 —— 那时块早就退出了，托管数组可能已经被 GC
        /// 搬走，ImGui 读到的就是别人的内存。
        /// </summary>
        private static IntPtr AllocRange(ushort[] range)
        {
            IntPtr pointer = Marshal.AllocHGlobal(range.Length * sizeof(ushort));
            // Marshal.Copy 没有 ushort[] 的重载，所以先按字节搬一份。
            byte[] bytes = new byte[range.Length * sizeof(ushort)];
            Buffer.BlockCopy(range, 0, bytes, 0, bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        /// <summary>
        /// 把一张 Unity 贴图登记给 ImGui 画，返回要传给 `AddImage` 的纹理 ID。
        ///
        /// **只登记引用，不复制像素。** 游戏自己的图标我们直接把游戏那张图集
        /// 登记进来、配它自己的 UV 矩形就能画了 —— 不拷像素、不落盘、不进包，
        /// 游戏一关就什么都没了。这也正是"只调用原版图标、不分发原版资源"。
        ///
        /// 登记过的贴图**不许 Destroy**（见 `_textures` 的说明）。
        /// </summary>
        internal static IntPtr RegisterTexture(Texture2D texture)
        {
            return RegisterTexture((Texture)texture);
        }

        /// <summary>
        /// 登记一张任意贴图（`RenderTexture` 也走这条 —— 粒子编辑器的预览就是它）。
        /// 调用方负责别把它 Destroy 掉。
        /// </summary>
        internal static IntPtr RegisterTexture(Texture texture)
        {
            EditorImGuiBridge bridge = Instance;
            if (bridge == null || texture == null)
            {
                return IntPtr.Zero;
            }
            IntPtr id = (IntPtr)texture.GetInstanceID();
            bridge._textures[id] = texture;
            return id;
        }

        /// <summary>
        /// 正文那个字体。
        ///
        /// 给"要用非默认字号画字"的地方用 —— 目前只有装饰物列表里图标的**文字兜底**
        /// 一处：它得和图标一样大，用默认字号（23）画会比旁边的图标大一圈。
        ///
        /// 连中文字体都没找到时它可能是空指针，用之前自己判一下。
        /// </summary>
        internal ImFontPtr UiFont
        {
            get { return _uiFont; }
        }

        /// <summary>小字号那一档（时间轴块名、标尺格号用）。</summary>
        internal ImFontPtr SmallFont
        {
            get { return _smallFont; }
        }

        /// <summary>
        /// ImGui 这会儿是不是有人在某个输入框里打字。
        ///
        /// 工作台的键盘处理器要问它 —— `EditorStudioOverlay.IsTextInputFocused`
        /// 只认 uGUI 的 TMP 输入框，而 ImGui 那些它看不见。不问的话，
        /// 在 ImGui 的框里按 Delete / Space 会一并落到谱面上。
        /// </summary>
        internal static bool WantsTextInput
        {
            get { return ImGui.GetIO().WantTextInput; }
        }

        /// <summary>小字号建出来了没有。用 SmallFont 之前先问它。</summary>
        internal unsafe bool HasSmallFont
        {
            get { return _smallFont.NativePtr != null; }
        }

        /// <summary>小字号那一档的字号（设计像素）。</summary>
        internal const float SmallFontSize = SmallFontPixels;

        /// <summary>
        /// 正文那个字体建出来了没有。**用 <see cref="UiFont"/> 之前必须先问它。**
        /// 指针比较要进 unsafe，所以封在这里，调用方不用整块方法挂 unsafe。
        /// </summary>
        internal unsafe bool HasUiFont
        {
            get { return _uiFont.NativePtr != null; }
        }

        // ---------------------------------------------------------------- 说明窗口的正文

        /// <summary>窗口标题兼"加群"那一行 —— 和 uGUI 那扇一模一样。</summary>
        private const string HelpTitle = "加入 Q 交流群 " + EditorStudioOverlay.QqGroupNumber;

        /// <summary>
        /// 版本说明窗口的标题。**不能写成 `const`** —— 版本号现在是从 UMM 读的
        /// 属性（见 `EditorStudioOverlay.ModVersion`），编译期定不下来。
        /// </summary>
        private static string VersionTitle
        {
            get { return "v" + EditorStudioOverlay.ModVersion + " 更新内容"; }
        }

        /// <summary>版本说明窗口（以及以后别的窗口）底部那条可点的加群链接。</summary>
        private const string HelpFooter = "点这里进 Q 群交流：" + EditorStudioOverlay.QqGroupNumber;

        /// <summary>
        /// 版本更新窗口的正文。记号和 <see cref="HelpLines"/> 同一套。
        ///
        /// 内容是从 uGUI 那扇 `VersionNotes` 搬过来的 —— **发版时两处一起改**：
        /// 桥接起不来时 uGUI 那扇是退路，两处文案不能各说各的。
        ///
        /// 标题那一行**没搬**：窗口标题栏写的已经是同一句话，正文里再来一遍是重复
        /// （uGUI 那边是卡片标题和正文各一份）。
        /// </summary>
        private static readonly string[] VersionLines =
        {
            "#新增",
            "*粒子编辑器：原版那扇面板在工作台里打不开（被两层画布压在下面），现在自己复刻了一扇 ——",
            "   通用 / 形状 / 变换三个页签，参数和原版同一串；底下是实时预览，改一个值画面当场变。",
            "*参数栏补齐了以前显示「暂不支持」的几类参数：粒子发射器、最小~最大浮点、",
            "   随时间变速（X / Y 两组范围）、渐变调色板、效果起止方块（数字 + 本格 / 起点 / 终点）。",
            "*框选：锚点钉在时间轴上。拖着框的时候用 Shift+滚轮滚时间轴、或 Ctrl+滚轮缩放，",
            "   框会跟着内容走，不再停在屏幕上不动。",
            "*框选：Ctrl+Shift+拖动是叠加框选，可以分几次框不同段落，之前的选中保留。",
            "",
            "#修好的",
            "*删除：框选或 Ctrl 点选进来的装饰物 / 文本，以前按 Delete 删不掉，已修正。",
            "*删除后撤销：事件能回来，但位置掉到时间轴最下面，已修正 —— 现在回到原来的行。",
            "*标尺：指在尺标上会穿透点到底下的事件块，已修正。",
            "*轨道颜色：经过一个改色事件后再退回它之前，颜色不还原，已修正。",
            "*下拉列表：展开后鼠标在列表里移动，下面的参数卡片跟着高亮（像能穿透），已修正。",
            "*渐变调色板：调一次色就变黑、怎么调都是黑，已修正。",
        };
        /// <summary>
        /// 说明窗口的正文。一项一行，**行首那个记号决定怎么画**：
        ///
        ///   `#`  小节标题（画成带一条分隔线的标题）
        ///   `*`  条目（前面一颗圆点，长句自动折行）
        ///   `|`  快捷键一条，格式是「按键|说明」—— 竖线切成左右两栏对齐
        ///   `-`  次要说明（灰字，用在标题底下那句）
        ///   `""` 空行
        ///   其它 普通段落（自动折行）
        ///
        /// 记号本身也会进字形范围表，无害（就四个 ASCII 字符）。
        ///
        /// 文案内容是从 uGUI 那扇说明窗口搬过来的，两边指的快捷键是同一套 ——
        /// 改了 `EditorPlaybackBridge` / `EditorTimelineView` 里的绑定，这里要跟着改。
        /// </summary>
        private static readonly string[] HelpLines =
        {
            "#ADOFAI Editor Studio",
            "-给《A Dance of Fire and Ice》编辑器加的一套工作台",
            "",
            "#这个 MOD 是什么",
            "在原版编辑器旁边加一整套工作台：预览、时间线、参数编辑、媒体库、设置页。",
            "预览由它自己的求值器驱动，所以拖时间轴时画面即时跟着走，不必真的按播放 ——"
                + "这是它和「开原版播放再看」最大的区别。",
            "",
            "#新功能",
            "*独立预览：拖动即出画面，不受原版播放 / 暂停状态影响。",
            "*时间线：事件按类型分节。拖块改时间与时长；拖动中块跟着光标走，"
                + "松手回到该在的行；默认吸附到格（按住 Alt 临时关掉）。",
            "*选择：Ctrl+左键点选加选、从空白处拖动框选；Ctrl+Shift+拖动是叠加框选"
                + "（不清掉前一批）；点空白处取消全部。"
                + "选中一批可以一起拖，各自的偏移不丢。",
            "*参数栏：原生全部事件参数，含缓动曲线；标签按原生那套三段式本地化，和游戏里一致。",
            "*调色盘：饱和 / 明度方块 + 色相条 + 透明度条 + HEX + RGBA。"
                + "拖动跟手，松手才写进谱面。",
            "*批量改参：多选后只列公共属性，改一次落到整批，只记一步撤销。",
            "*装饰物：左栏列表管理；媒体库里的图片直接拖进画面即成装饰物。",
            "*设置页：歌曲 / 关卡 / 轨道 / 背景 / 摄像头 / 杂项，在工作台里直接改。",
            "*撤销重做：工作台有自己的历史 —— 拖进来的装饰物、批量改动、"
                + "拖块改的时间，都能一次撤回。",
            "*Ctrl+S 保存：走原生 SaveLevel，存出来和原版逐字节一样。",
            "",
            "#快捷键",
            "|悬浮球|拖动可移动；单击打开 / 关闭工作台",
            "|Esc|逐层关闭（粒子编辑器 → 弹窗 → 工作台；游玩中则是退出游玩）",
            "|Space|播放 / 暂停",
            "|Ctrl+Z|撤销",
            "|Ctrl+Y|重做（Ctrl+Shift+Z 同）",
            "|Ctrl+S|保存",
            "|Ctrl+C / X / V|复制 / 剪切 / 粘贴选中的事件",
            "|Delete / Backspace|删除选中",
            "|Shift+C|清除全部选中",
            "|Ctrl+左键|点选加选；从空白处拖动则框选",
            "|Ctrl+Shift+拖动|叠加框选（保留之前选中的）",
            "|点空白处|清除全部选中",
            "",
            "#时间轴",
            "|滚轮|上下滚；没有纵向可滚时左右滚",
            "|Shift+滚轮|左右滚",
            "|中键拖动|平移",
            "|Ctrl+滚轮|缩放（Ctrl+= / Ctrl+- 同，Ctrl+0 复位）",
            "|Alt+滚轮|行高（Alt+= / Alt+- 同）",
            "|点标尺 / 播放头|拖动定位",
            "",
            "#调色盘",
            "拖方块 / 色相条 / 透明度条选色，松手才写进谱面；"
                + "HEX 和 R G B A 也可以直接输入。",
            "",
        };

        private enum HelpKind
        {
            Heading,
            Note,
            Bullet,
            KeyValue,
            Paragraph,
            Spacer,
        }

        private sealed class HelpRow
        {
            internal HelpKind Kind;
            internal string Key;
            internal string Text;
        }

        /// <summary>正文解析后的结果：画的时候只读它，不再碰字符串。</summary>
        private static HelpRow[] _helpRows;
        private static HelpRow[] _versionRows;

        /// <summary>
        /// 把一套正文拆成好画的一行行（说明窗口和版本说明窗口共用）。
        ///
        /// **拆的动作只做一次、放在初始化里，不在每帧画的时候现拆。**
        /// ImGui 的 `Begin` 和 `End` 之间只要抛一次异常，这一帧就 Render 不掉，
        /// 下一帧 `NewFrame` 会直接触发原生断言（就是那个
        /// "Forgot to call Render()" 的弹框）—— 而 ImGui 的弹框是 Windows 模态框，
        /// 每帧弹一次，游戏就没法玩了。文案又长，拆错一个下标就可能中招
        /// （第一版就是 `IndexOf('|')` 找到行首那个记号、`Substring(1, -1)` 抛出去的）。
        /// </summary>
        private static HelpRow[] BuildRows(string[] lines)
        {
            System.Collections.Generic.List<HelpRow> rows
                = new System.Collections.Generic.List<HelpRow>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                HelpRow row = new HelpRow();
                row.Text = string.Empty;
                if (line.Length == 0)
                {
                    row.Kind = HelpKind.Spacer;
                    rows.Add(row);
                    continue;
                }

                switch (line[0])
                {
                    case '#':
                        row.Kind = HelpKind.Heading;
                        row.Text = line.Substring(1);
                        break;
                    case '-':
                        row.Kind = HelpKind.Note;
                        row.Text = line.Substring(1);
                        break;
                    case '*':
                        row.Kind = HelpKind.Bullet;
                        // 条目按**第一个全角冒号**切成"功能名 + 说明"两段：
                        // 功能名用强调色，说明用正文色。
                        // 切在这里（初始化）而不是每帧画的时候，理由同上。
                        row.Text = line.Substring(1);
                        int colon = row.Text.IndexOf('：');
                        if (colon > 0)
                        {
                            row.Key = row.Text.Substring(0, colon);
                            row.Text = row.Text.Substring(colon);
                        }
                        break;
                    case '|':
                        // 注意从下标 1 开始找：**下标 0 那个就是记号本身**。
                        int bar = line.IndexOf('|', 1);
                        if (bar < 0)
                        {
                            row.Kind = HelpKind.Paragraph;
                            row.Text = line;
                        }
                        else
                        {
                            row.Kind = HelpKind.KeyValue;
                            row.Key = line.Substring(1, bar - 1);
                            row.Text = line.Substring(bar + 1);
                        }
                        break;
                    default:
                        row.Kind = HelpKind.Paragraph;
                        row.Text = line;
                        break;
                }
                rows.Add(row);
            }
            return rows.ToArray();
        }

        // ---------------------------------------------------------------- 启停

        private void Awake()
        {
            Instance = this;

            _assemblyDirectory = Path.GetDirectoryName(typeof(EditorImGuiBridge).Assembly.Location);
            if (!PreloadNative(_assemblyDirectory))
            {
                enabled = false;
                return;
            }

            // **全局上下文是借来的，建完要还。**
            //
            // ImGui 是进程级的全局状态机，同一个进程里可能还有别的 mod 也在用
            // （CheryTools 就是）。`CreateContext()` 会把新上下文直接设成"当前"，
            // 我们建字体图集必须跑在自己的上下文里 —— 但建完不退回去的话，
            // 别人的上下文就被我们永久顶掉：它后面每一帧的 NewFrame / GetDrawData
            // 都跑在我们的上下文上，文字拿着我们的字体图集 UV 去采样，画出来
            // 整片乱码（社区反馈的就是这个）。
            IntPtr previousContext = ImGui.GetCurrentContext();
            try
            {
                BuildImGui();
                _ready = true;
                Main.Logger.Log("[ImGui] 桥接就绪，cimgui 已加载");
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[ImGui] 初始化失败：" + exception);
                enabled = false;
            }
            finally
            {
                // 拿到的如果是空，说明没别人在用，那就留着自己这份 ——
                // 空上下文会让任何一次 GetIO 直接踩空指针。
                if (previousContext != IntPtr.Zero)
                {
                    ImGui.SetCurrentContext(previousContext);
                }
            }
        }

        private void BuildImGui()
        {
            _context = ImGui.CreateContext();
            ImGui.SetCurrentContext(_context);

            ImGuiIOPtr io = ImGui.GetIO();
            // 不写 imgui.ini —— 工作台的位置由我们自己存，不让它到处落文件。
            unsafe
            {
                ((ImGuiIO*)io.NativePtr)->IniFilename = null;
            }

            // 缩放要先算出来：字体是按"最终屏幕像素"光栅化的，建字体那一步就要用。
            // 公式和 Update 里那句是同一个（见那边的说明）。
            _scale = Screen.height > 0 ? Screen.height / DesignHeight : 1f;

            BuildFont(io);
            _helpRows = BuildRows(HelpLines);
            _versionRows = BuildRows(VersionLines);
            _header = new EditorImGuiHeader();
            _menuBar = new EditorImGuiMenuBar();
            _previewPanel = new EditorImGuiPreviewPanel();
            _timelineHeader = new EditorImGuiTimelineHeader();
            _eventPalette = new EditorImGuiEventPalette();
            _timelineBody = new EditorImGuiTimelineBody();
            _inspector = new EditorImGuiInspector(false);
            _settingsInspector = new EditorImGuiInspector(true);
            _decorationList = new EditorImGuiDecorationList();
            _mediaPanel = new EditorImGuiMediaPanel();
            ApplyTheme();
            // 画布：屏幕覆盖层，排在最上面。sortingOrder 给到 short 的最大值，
            // 免得被游戏自己的界面（包括我们现有的 uGUI 工作台）盖住。
            //
            // **不要再往上加**：Unity 排画布用的是 short，超过 32767 会翻成负数
            // 沉到最底 —— 加载页踩过这个坑（黑屏整个被工作台盖住，看着像"没覆盖全"）。
            // 加载页要盖住这一层的时候，是**把这一层临时压下去**，不是把那边抬上来，
            // 见 `SetOverlaySuppressed`。
            // 不挂 CanvasScaler —— ScreenSpaceOverlay 默认就是恒定像素，够了。
            GameObject host = new GameObject("ADOFAIEditorStudio.ImGui");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _canvas = host.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = OverlaySortingOrder;

            // ImGui 画的是带顶点色的三角形，Unity 自带的 UI/Default 正好吃这一套
            // （它的顶点格式就是 位置 + 颜色 + UV）。
            Shader uiShader = Shader.Find("UI/Default");
            if (uiShader == null)
            {
                // 找不到的话材质是空的，画出来只会是一团错色 —— 先喊出来，
                // 不然会误判成字体问题。
                throw new InvalidOperationException("找不到 UI/Default shader");
            }
            _material = new Material(uiShader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = _fontTexture
            };

            BuildBlocker(host);

            Main.Logger.Log("[ImGui] 材质就绪，shader=" + uiShader.name);
        }

        /// <summary>
        /// 模态遮罩：一扇 ImGui 窗口底下的那块压暗层。
        ///
        /// **这一块非有不可。** ImGui 画出来的是 Mesh + CanvasRenderer，不是
        /// uGUI 的 Graphic —— 也就是说 **uGUI 的射线根本不认识它**。窗口底下
        /// 如果还摆着工作台的按钮，点说明窗口会同时点到背后那些控件，
        /// 在看不见的地方把东西改了。所以自己挂一块全屏 Image 兜住点击。
        ///
        /// 它建在画布上、并且**排在所有 ImGui 网格之前**（网格是每帧现建的，
        /// 一定在它后面），于是它挡住点击、而窗口画在它上面。
        ///
        /// 颜色和 uGUI 那扇窗口的遮罩一致（黑、55%），所以搬过来以后
        /// 底下的工作台压暗程度看不出区别。
        /// </summary>
        private void BuildBlocker(GameObject host)
        {
            // uGUI 要能打中这块 Image，画布上必须有个 GraphicRaycaster。
            host.AddComponent<GraphicRaycaster>();

            GameObject blocker = new GameObject("ImGuiBlocker", typeof(RectTransform));
            blocker.transform.SetParent(_canvas.transform, false);
            RectTransform rect = (RectTransform)blocker.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _blocker = blocker.AddComponent<Image>();
            _blocker.color = new Color(0f, 0f, 0f, 0.55f);
            _blocker.raycastTarget = true;
            blocker.SetActive(false);

            BuildPanelBlocker();
        }

        /// <summary>
        /// 常驻面板的挡点击层：一块**全透明**的 Image，位置和大小每帧跟着面板走。
        ///
        /// 为什么全透明也管用：uGUI 的射线只看 `raycastTarget`，不看颜色 ——
        /// 透明不等于"点不到"。所以它不出现在画面上，却能把点击接住。
        ///
        /// 它和上面那块模态遮罩是**两件事**：那块是"整屏压暗、模态"，
        /// 这块是"只占一块地方、不压暗"。
        /// </summary>
        private void BuildPanelBlocker()
        {
            GameObject blocker = new GameObject("ImGuiPanelBlocker", typeof(RectTransform));
            blocker.transform.SetParent(_canvas.transform, false);
            RectTransform rect = (RectTransform)blocker.transform;
            // 锚在**左上角**：面板的坐标是"从设计区左上角量"的（和 ImGui 一致），
            // 画布自己的坐标系是中心原点，所以只能这样锚，不能拉伸。
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);

            _panelBlocker = blocker.AddComponent<Image>();
            _panelBlocker.color = new Color(0f, 0f, 0f, 0f);
            _panelBlocker.raycastTarget = true;
            blocker.SetActive(false);

            // 左栏那一块，做法相同、位置不同。
            _decoBlocker = CreateBlocker("ImGuiDecoBlocker");
            _mediaBlocker = CreateBlocker("ImGuiMediaBlocker");
            _settingsBlocker = CreateBlocker("ImGuiSettingsBlocker");
            _headerBlocker = CreateBlocker("ImGuiHeaderBlocker");
            _previewBlocker = CreateBlocker("ImGuiPreviewBlocker");
            _timelineHeaderBlocker = CreateBlocker("ImGuiTimelineHeaderBlocker");
            _eventPaletteBlocker = CreateBlocker("ImGuiEventPaletteBlocker");
            _timelineBodyBlocker = CreateBlocker("ImGuiTimelineBodyBlocker");
            // 悬浮球那一块：它工作台关着时也在，所以单独一份。
            _ballBlocker = CreateBlocker("ImGuiFloatingBallBlocker");
            _particleBlocker = CreateBlocker("ImGuiParticleEditorBlocker");
        }

        private Image CreateBlocker(string name)
        {
            GameObject blocker = new GameObject(name, typeof(RectTransform));
            blocker.transform.SetParent(_canvas.transform, false);
            RectTransform rect = (RectTransform)blocker.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            Image image = blocker.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;
            blocker.SetActive(false);
            return image;
        }

        /// <summary>
        /// 加载页要显示时，把 ImGui 这一层临时压到底。
        ///
        /// **为什么不干脆让加载页排得更高**：`sortingOrder` 是 short，32767 就是顶，
        /// 两边都已经贴着上限，谁也高不过谁（加载页写 32768 会翻成负数沉底，这个坑
        /// 刚踩过）。所以反过来做 —— 加载期间把工作台整层压到 0，加载页占住 32767，
        /// 天然就在最上面。
        ///
        /// 反正加载页存在的意义就是「别让用户看见中间那些半成品状态」，工作台本来
        /// 也不该在这时候露出来。加载完自动抬回原值。
        ///
        /// 挡点击的那几层 blocker 是这块画布的子物体，会跟着一起沉，不用单独管。
        /// </summary>
        internal void SetOverlaySuppressed(bool suppressed)
        {
            if (_canvas == null)
            {
                return;
            }
            int wanted = suppressed ? 0 : OverlaySortingOrder;
            if (_canvas.sortingOrder != wanted)
            {
                _canvas.sortingOrder = wanted;
            }
        }

        private bool _suppressForLoading;
        private bool _suppressForParticleEditor;

        /// <summary>加载页显隐时压 / 抬工作台那层。</summary>
        internal void SetOverlaySuppressedForLoading(bool suppressed)
        {
            _suppressForLoading = suppressed;
            ApplyOverlaySuppression();
        }

        /// <summary>
        /// 原版的粒子编辑器开着时压 / 抬工作台那层。
        ///
        /// **为什么需要**：粒子编辑器是原生 uGUI 面板，而我们的 ImGui 画布在
        /// `sortingOrder = 32000` —— 它被工作台整层盖住（尤其中间那块预览区），
        /// 表现就是「点『编辑』什么都没出现」。
        /// 两个来源（加载页 / 粒子编辑器）各自记一个标志再合并，
        /// 否则谁后关谁就会把另一个的压层也一起抬起来。
        /// </summary>
        internal void SetOverlaySuppressedForParticleEditor(bool suppressed)
        {
            _suppressForParticleEditor = suppressed;
            ApplyOverlaySuppression();
        }

        private void ApplyOverlaySuppression()
        {
            SetOverlaySuppressed(_suppressForLoading || _suppressForParticleEditor);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            if (_context != IntPtr.Zero)
            {
                // 销毁前先记下别人是谁：`DestroyContext` 会把"当前上下文"清成空，
                // 不退回去的话，同在进程里的 CheryTools 下一次 NewFrame 就没上下文了。
                IntPtr dyingContext = _context;
                IntPtr previousContext = ImGui.GetCurrentContext();
                ImGui.SetCurrentContext(dyingContext);
                ImGui.DestroyContext(dyingContext);
                _context = IntPtr.Zero;
                // 拿到的如果是空、或者就是我们自己（刚销毁），都别再设回去 ——
                // 那是悬空指针。
                if (previousContext != IntPtr.Zero && previousContext != dyingContext)
                {
                    ImGui.SetCurrentContext(previousContext);
                }
            }
            for (int i = 0; i < _meshes.Count; i++)
            {
                if (_meshes[i] != null)
                {
                    UnityEngine.Object.Destroy(_meshes[i]);
                }
            }
            _meshes.Clear();
            _renderers.Clear();
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
            }
            if (_fontTexture != null)
            {
                UnityEngine.Object.Destroy(_fontTexture);
            }
            // 只清我们自己建的那几张手绘图标贴图。`_textures` 里还借了游戏
            // 自己的图集，那些一个都不能 Destroy（见 `_textures` 的说明）。
            EditorImGuiIcons.ReleaseDrawn();
            _textures.Clear();
        }

        private void OnApplicationQuit()
        {
            _ready = false;
        }

        // ---------------------------------------------------------------- 皮肤

        /// <summary>
        /// 把 ImGui 的皮肤接到工作台自己那套 token 上（<see cref="Palette"/>）。
        ///
        /// **不去套网上的主题包。** 主题包是别人照自己界面的尺寸和配色调的，
        /// 套上来只会和旁边那些 uGUI 面板对不上；而且以后调色要改两个地方，
        /// 迟早会漂移。从 `UiTheme` 推出来，"两边是同一套界面"这件事就是
        /// **结构上**成立的 —— 换主题时改 `UiTheme`，两套一起变。
        ///
        /// 圆角也不再自己编，直接沿用工作台那两档（`CornerLarge` 给窗口、
        /// `CornerSmall` 给控件）。因为坐标系已经统一到 1920x1080 设计空间，
        /// 这些数字在两边是同一个意思，抄过来就是对的。
        /// </summary>
        private static void ApplyTheme()
        {
            ImGuiStylePtr style = ImGui.GetStyle();

            style.WindowRounding = EditorStudioOverlay.CornerLarge;
            style.PopupRounding = EditorStudioOverlay.CornerSmall;
            style.FrameRounding = EditorStudioOverlay.CornerSmall;
            style.GrabRounding = EditorStudioOverlay.CornerSmall;
            style.ScrollbarRounding = EditorStudioOverlay.CornerSmall;
            style.WindowBorderSize = 1f;
            style.FrameBorderSize = 0f;
            style.WindowPadding = new NVec2(UiSpacing.Lg, UiSpacing.Md);
            style.FramePadding = new NVec2(10f, 6f);
            style.ItemSpacing = new NVec2(UiSpacing.Sm, UiSpacing.Sm);
            style.ItemInnerSpacing = new NVec2(UiSpacing.Sm, UiSpacing.Xs);
            style.ScrollbarSize = 12f;

            SetColor(style, ImGuiCol.Text, Palette.TextPrimary);
            SetColor(style, ImGuiCol.TextDisabled, Palette.TextMuted);
            SetColor(style, ImGuiCol.WindowBg, Palette.Panel);
            SetColor(style, ImGuiCol.ChildBg, Palette.Panel);
            SetColor(style, ImGuiCol.PopupBg, Palette.Raised);
            // 描边和分隔线都用那条发丝线：它只是兜个边界，不是要人看见的东西。
            SetColor(style, ImGuiCol.Border, Palette.PanelRule);
            SetColor(style, ImGuiCol.Separator, Palette.PanelRule);
            SetColor(style, ImGuiCol.SeparatorHovered, Palette.Accent);
            SetColor(style, ImGuiCol.SeparatorActive, Palette.Accent);
            // 输入凹槽比面板暗（"凹"下去），凸起的面比面板亮 —— 和 uGUI 同一套读法。
            SetColor(style, ImGuiCol.FrameBg, Palette.FieldWell);
            SetColor(style, ImGuiCol.FrameBgHovered, Palette.Raised);
            SetColor(style, ImGuiCol.FrameBgActive, Palette.Raised);
            SetColor(style, ImGuiCol.TitleBg, Palette.SurfaceBand);
            SetColor(style, ImGuiCol.TitleBgActive, Palette.SurfaceBand);
            SetColor(style, ImGuiCol.TitleBgCollapsed, Palette.Panel);
            SetColor(style, ImGuiCol.Button, Palette.ButtonNormal);
            SetColor(style, ImGuiCol.ButtonHovered, Palette.ButtonHover);
            SetColor(style, ImGuiCol.ButtonActive, Palette.Raised);
            SetColor(style, ImGuiCol.Header, Palette.ButtonNormal);
            SetColor(style, ImGuiCol.HeaderHovered, Palette.ButtonHover);
            SetColor(style, ImGuiCol.HeaderActive, Palette.Selected);
            // 蓝色的地方就是这两个：能点的（勾、滑杆）和选中的。
            SetColor(style, ImGuiCol.CheckMark, Palette.Accent);
            SetColor(style, ImGuiCol.SliderGrab, Palette.Accent);
            SetColor(style, ImGuiCol.SliderGrabActive, Palette.Accent);
            SetColor(style, ImGuiCol.ScrollbarBg, Palette.Background);
            SetColor(style, ImGuiCol.ScrollbarGrab, Palette.Raised);
            SetColor(style, ImGuiCol.ScrollbarGrabHovered, Palette.ButtonHover);
            SetColor(style, ImGuiCol.ScrollbarGrabActive, Palette.Accent);
            SetColor(style, ImGuiCol.TextSelectedBg, Palette.Selected);
            // 表格：边框用和工作台其它描边同一条发丝线，别用 ImGui 默认那套浅灰。
            SetColor(style, ImGuiCol.TableBorderStrong, Palette.PanelRule);
            SetColor(style, ImGuiCol.TableBorderLight, Palette.PanelRule);
            SetColor(style, ImGuiCol.TableHeaderBg, Palette.SurfaceBand);
            // 键盘/手柄导航的那个框。（1.91 把它从 NavHighlight 改名成 NavCursor。）
            SetColor(style, ImGuiCol.NavCursor, Palette.Accent);
        }

        private static void SetColor(ImGuiStylePtr style, ImGuiCol slot, Color color)
        {
            style.Colors[(int)slot] = new NVec4(color.r, color.g, color.b, color.a);
        }

        // ---------------------------------------------------------------- 字体

        /// <summary>
        /// 正文字号。
        ///
        /// **23 是照 uGUI 正文那档配的**（`UiType` 里正文 15~16，设计空间 1080 高）
        /// —— 乘上约 0.7 的换算，落到屏幕上两边一样大。
        /// 之前给的是 32，屏幕上明显比旁边那些面板大一截。
        ///
        /// 嫌大嫌小都只改这一个数。
        /// </summary>
        private const float BodyFontPixels = 23f;

        /// <summary>
        /// 小字号那一档（时间轴事件块的名字、标尺格号用的）。
        ///
        /// 图集里**每一档字号都是一个独立的字形位图** —— ImGui 缩小画是糊的，
        /// 所以只能多带一档。代价是图集变大，部署后看日志里那行"图集"就知道
        /// 涨到多少了。
        /// </summary>
        private const float SmallFontPixels = 15f;

        /// <summary>
        /// 建字体图集，并把它做成一张 Unity 贴图。
        ///
        /// **中文必须给 TTF 文件。** ImGui 自带的默认字体（ProggyClean）只有
        /// ASCII 和拉丁文，光调 `AddFontDefault` 是画不出汉字的 —— 这是刚接入
        /// 时最容易踩的坑。
        ///
        /// 用随包带的 `UiFont.ttf`（975 圆体的子集，OFL），不是系统字体 ——
        /// 见下面挑字体那一段的说明。
        /// </summary>
        private void BuildFont(ImGuiIOPtr io)
        {
            // **先找随包带的那份子集字体 —— 发布版走的必须是它。**
            //
            // 两条理由：不能指望用户机器上装了什么中文字体；而且界面观感要固定，
            // 不能"碰上什么用什么"。那份是 `tools/make-font-subset.py` 从
            // 975 圆体（思源黑体的简体字形 + 圆角，OFL）裁出来的，只留通用规范
            // 汉字表一级那三千五百字加上标点和 ASCII 字面量，17.4MB → 1.3MB。
            // OFL 要求随字体附带许可原文，就是旁边的 `UiFont-LICENSE.txt`。
            //
            // 字表不够用时（搬面板带来新字）重跑那个脚本，别手改字体文件。
            string picked = null;
            string bundled = Path.Combine(_assemblyDirectory ?? string.Empty, "UiFont.ttf");
            if (File.Exists(bundled))
            {
                picked = bundled;
            }

            // 开发机上万一没把字体带上，退回系统里那几个中文字体 ——
            // 观感不一样，但总好过画出一片方框。
            if (picked == null)
            {
                string[] candidates =
                {
                    @"C:\Windows\Fonts\msyh.ttc",     // 微软雅黑
                    @"C:\Windows\Fonts\simhei.ttf",   // 黑体
                    @"C:\Windows\Fonts\deng.ttf",     // 等线
                };
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (File.Exists(candidates[i]))
                    {
                        picked = candidates[i];
                        break;
                    }
                }
                if (picked != null)
                {
                    Main.Logger.Warning("[ImGui] 包里没有 UiFont.ttf，退回系统字体：" + picked);
                }
            }

            if (picked == null)
            {
                io.Fonts.AddFontDefault();
                Main.Logger.Error("[ImGui] 没找到中文字体，退回默认字体（画不出汉字）");
            }
            else
            {
                // **字号：`SizePixels` 不是"字身的宽高"。**
                //
                // 它映射的是字体的 `(ascent − descent)`，中文实际量出来大约只有
                // 这个数的 **0.7 倍**（微软雅黑给 32 → 约 22px）。所以"看着比号数
                // 小"是预期的，不是 bug —— 挑字号时按实际效果挑，别按数字推。
                // 具体取多少见 `BodyFontPixels`。
                //
                // **字形范围自己从界面文案里提**（见 `UiText`），不用
                // `GetGlyphRangesChineseSimplifiedCommon` —— 那个 API 一次给两千多字，
                // 图集白白撑大十几倍，而界面文案加起来也就几十个字。
                //
                // **`ImFontConfig` 必须显式建、字号必须写进去**：传
                // `default(ImFontConfigPtr)`（等于 C++ 的 NULL）时它用的是 ImGui
                // 自己的默认配置，实测拿到的字号不是我们传的那个。
                // **位图按"屏幕像素"出，不按设计像素出。**
                //
                // 界面排的是 1920x1080 设计空间，而屏幕是 2560x1440 —— 整屏要放大
                // 1.333 倍。字体要是按设计尺寸（em 约 16px）光栅化、再被拉成 21px，
                // 那就是把 16px 的位图放大三分之一，细笔画边缘必然发虚。
                //
                // 所以位图直接按**最终像素尺寸**出（em × 1.333），再用
                // `FontGlobalScale` 把排版尺寸压回设计尺寸：位图与屏幕像素一一对应，
                // 字就实了。排版用的尺寸没变，所以"搬面板时坐标能直接抄 uGUI"不受影响。
                //
                // 代价是图集面积大 1.78 倍 —— 我们只收几百个字，无所谓。
                float rasterPixels = BodyFontPixels * _scale;
                ushort[] glyphRange = BuildUiGlyphRange();
                IntPtr range = AllocRange(glyphRange);
                unsafe
                {
                    ImFontConfigPtr cfg = ImGuiNative.ImFontConfig_ImFontConfig();
                    try
                    {
                        cfg.SizePixels = rasterPixels;
                        // **横向超采样关掉。**
                        //
                        // 它本来是为了横向抗锯齿，但位图已经按**最终屏幕像素**光栅化了
                        // （见上面那段），而 PixelSnapH 又把笔位对齐到整像素 ——
                        // 这时候再横向 2 倍采样几乎看不出差别，图集宽度却要翻一倍。
                        //
                        // 收了一级+二级共 6500 个汉字之后，这一倍决定了图集是
                        // 2048x2048（16MB 显存）还是 4096x2048（32MB）。
                        cfg.OversampleH = 1;
                        cfg.OversampleV = 1;
                        cfg.PixelSnapH = true;
                        _uiFont = io.Fonts.AddFontFromFileTTF(
                            picked, rasterPixels, cfg, range);
                    }
                    finally
                    {
                        ImGuiNative.ImFontConfig_destroy(cfg);
                    }
                    ((ImGuiIO*)io.NativePtr)->FontDefault = (ImFont*)_uiFont.NativePtr;
                }
                // 把排版尺寸压回设计尺寸（见上面那段说明）。
                io.FontGlobalScale = 1f / _scale;
                Main.Logger.Log("[ImGui] 字体：" + picked
                    + "  位图 " + BodyFontPixels + "x" + _scale.ToString("0.###")
                    + "=" + rasterPixels.ToString("0.#") + "px"
                    + "  排版 " + BodyFontPixels + "px  缩放 " + _scale.ToString("0.###")
                    + "  字形 " + (glyphRange.Length / 2) + " 段");
            }

            // 小字号那一档。**和正文用同一份字表和同一张图集** ——
            // 只是多一批更小的位图，纹理不另开一张。
            if (picked != null)
            {
                ushort[] smallRange = BuildUiGlyphRange();
                unsafe
                {
                    ImFontConfigPtr smallCfg = ImGuiNative.ImFontConfig_ImFontConfig();
                    try
                    {
                        smallCfg.SizePixels = SmallFontPixels * _scale;
                        smallCfg.OversampleH = 1;
                        smallCfg.OversampleV = 1;
                        smallCfg.PixelSnapH = true;
                        _smallFont = io.Fonts.AddFontFromFileTTF(
                            picked, SmallFontPixels * _scale, smallCfg,
                            AllocRange(smallRange));
                    }
                    finally
                    {
                        ImGuiNative.ImFontConfig_destroy(smallCfg);
                    }
                }
            }

            IntPtr pixels;
            int width, height, bytesPerPixel;
            // 计时：**这一句才真正去光栅化所有字形**。收了一整张汉字表之后
            // 这一步不再是瞬间的事（三千多个字），量出来记在日志里，慢了再想办法。
            long buildStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            io.Fonts.GetTexDataAsRGBA32(out pixels, out width, out height, out bytesPerPixel);
            long buildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - buildStarted)
                * 1000L / System.Diagnostics.Stopwatch.Frequency;

            int total = width * height * bytesPerPixel;
            byte[] buffer = new byte[total];
            Marshal.Copy(pixels, buffer, 0, total);

            // 不翻转，直接上传。
            //
            // 两边其实"都翻了一次、正好抵消"：ImGui 的图集第 0 行在顶部，
            // 而 Unity 的贴图内存第 0 行在底部（LoadRawTextureData 按底层布局拷），
            // 于是 ImGui 说的 uv.y=0（它认为的顶）在 Unity 里采样到的正好也是顶部。
            // 之前我多翻了一次，字形就整个倒过来（判定依据是把图集导出来看）。
            _fontTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _fontTexture.LoadRawTextureData(buffer);
            _fontTexture.Apply();

            // Unity 这边用 InstanceID 当纹理 ID —— 顶点里那个 TextureId 只有我们
            // 自己看得懂，绘制时靠它反查回 Texture 对象。
            // **字体图集的 ID 故意设成 0。**
            //
            // ImGui 里「没有显式指定纹理」的纯色几何（矩形、线条）用的就是 0，
            // 而文字用的是字体图集的 ID。两者不相等 → ImGui 在**每次矩形和文字
            // 交替**时都要拆一条绘制命令。时间轴上每个块都是「矩形 + 文字」，
            // 2286 个块就拆出好几千条；而每一条在 Unity 侧都要一个 Mesh +
            // CanvasRenderer，实测 3150 条命令、搬运 6.9ms/帧。
            //
            // 把字体图集也设成 0，两者就是同一个纹理，ImGui 不再拆。
            // 桥那边本来就有「纹理 ID 认不出来就一律退回字体图集」的兜底
            // （见 PushToCanvas），所以 0 会被正确解析成字体图集，不用另注册。
            io.Fonts.SetTexID(IntPtr.Zero);
            io.Fonts.ClearTexData();

            Main.Logger.Log("[ImGui] 图集 " + width + "x" + height
                + " bpp=" + bytesPerPixel + "  建图 " + buildMs + "ms"
                + "  屏幕 " + Screen.width + "x" + Screen.height);
        }

        // ---------------------------------------------------------------- 对外接口

        /// <summary>说明窗口开着没有。</summary>
        internal bool HelpVisible
        {
            get { return _helpOpen; }
        }

        /// <summary>
        /// 桥接有没有真正跑起来（cimgui 加载 + 图集建成功）。
        ///
        /// 工作台那边按它决定走 ImGui 还是退回 uGUI —— 没有这一句的话，
        /// 初始化失败时按钮会变成"按了没反应"。
        /// </summary>
        internal bool IsReady
        {
            get { return _ready; }
        }

        internal void SetHelpVisible(bool show)
        {
            _helpOpen = show && _ready;
        }

        internal void ToggleHelp()
        {
            SetHelpVisible(!_helpOpen);
        }

        /// <summary>版本说明窗口开着没有。</summary>
        internal bool VersionVisible
        {
            get { return _versionOpen; }
        }

        internal void SetVersionVisible(bool show)
        {
            if (!show && _versionOpen)
            {
                // 是用户自己关的（×、Esc、或者点了左上角版本号那一手）——
                // 要回报给工作台，让它记下"这个版本的更新内容看过了"。
                _versionClosed = true;
            }
            _versionOpen = show && _ready;
        }

        /// <summary>
        /// 推开「创建工程」那一屏。默认名和路径都由工作台算好传进来 ——
        /// 只有它知道谱面在哪、目录按哪套约定拼。
        /// </summary>
        internal void OpenProjectCreate(string defaultName, string path)
        {
            if (!_ready)
            {
                return;
            }
            _projectName = defaultName ?? "";
            _projectPath = path ?? "";
            _projectCreateConfirmed = false;
            _projectCreateCancelled = false;
            _projectCreateOpen = true;
        }

        /// <summary>创建工程那一屏开着没有（工作台用它决定要不要挡别的输入）。</summary>
        internal bool ProjectCreateVisible
        {
            get { return _projectCreateOpen; }
        }

        /// <summary>
        /// 把这一屏当「取消」关掉。Esc 和标题栏那个 × 都走这条 ——
        /// 它是个**要答复的问题**，关掉就等于回答"不建"。
        /// </summary>
        internal void CancelProjectCreate()
        {
            if (!_projectCreateOpen)
            {
                return;
            }
            _projectCreateCancelled = true;
            _projectCreateOpen = false;
        }

        /// <summary>
        /// 取一次用户在这一屏上的答复（取完就清）。返回 true 表示确实有答复：
        /// `confirmed` 为真 = 建（`name` 是他填的名字），为假 = 取消。
        ///
        /// 和 `TakeVersionClosed` 同一个套路：窗口是被自己的 × 关掉的，
        /// 工作台收不到通知，只能这样回报。
        /// </summary>
        internal bool TakeProjectCreateResult(out string name, out bool confirmed)
        {
            name = _projectName;
            confirmed = _projectCreateConfirmed;
            if (!_projectCreateConfirmed && !_projectCreateCancelled)
            {
                return false;
            }
            _projectCreateConfirmed = false;
            _projectCreateCancelled = false;
            return true;
        }

        /// <summary>推开「布局还没存」那扇窗。</summary>
        internal void OpenConfirmSave()
        {
            if (!_ready)
            {
                return;
            }
            _confirmSaveAnswer = 0;
            _confirmSaveOpen = true;
        }

        internal bool ConfirmSaveVisible
        {
            get { return _confirmSaveOpen; }
        }

        /// <summary>
        /// 取一次那扇窗的答复（取完就清）。返回 true 表示确实答了：
        /// 1 = 保存，2 = 不保存，3 = 取消。
        /// </summary>
        internal bool TakeConfirmSaveAnswer(out int answer)
        {
            answer = _confirmSaveAnswer;
            if (_confirmSaveAnswer == 0)
            {
                return false;
            }
            _confirmSaveAnswer = 0;
            return true;
        }

        /// <summary>
        /// 取一次「用户刚把版本说明关掉了」这个信号（取完就清）。
        ///
        /// 用"取一次"的写法是因为**关整个工作台顺手关掉它不算看过** ——
        /// uGUI 那边也是这样：关工作台不会把更新内容标记成已读，下次开还弹。
        /// </summary>
        internal bool TakeVersionClosed()
        {
            bool closed = _versionClosed;
            _versionClosed = false;
            return closed;
        }

        // ---------------------------------------------------------------- 每帧

        private void Update()
        {
            if (!_ready)
            {
                return;
            }

            // **每帧借一下全局上下文，走完还回去。**
            //
            // ImGui 是全局状态机：GetIO / NewFrame / GetFont / GetDrawData
            // 全都作用于「当前上下文」，而它是**进程级**的，别的 mod 也在用。
            //
            // 关键是 CheryTools 只在初始化时 SetCurrentContext 一次，绘制时不再设
            // —— 也就是说「谁最后 Set，谁的上下文就是全局的」。我们原来每帧抢过来
            // 就不还，它随后 NewFrame / GetDrawData 全跑在我们的上下文上：文字拿
            // 我们的字体图集 UV 去采样，画出来整片乱码（社区反馈的正是这个）。
            //
            // 存下进来时别人是谁、走完放回去，两边各画各的就互不干扰了。
            // 拿到的如果是空，说明没别人在用，那留着自己这份 —— 空上下文一 GetIO 就崩。
            IntPtr previousContext = ImGui.GetCurrentContext();
            ImGui.SetCurrentContext(_context);

            // ImGui 的坐标系按 1920x1080 排，和现有 uGUI 界面用同一把尺子 ——
            // 屏幕多大都换算到这个设计空间里，于是同一份尺寸数字两边含义一致，
            // 搬面板时可以直接把 uGUI 的坐标抄过来。
            // （uGUI 那边是 CanvasScaler 的 MatchHeight 行为，这里跟着它。）
            //
            // 注意：**字号不跟着这个缩放走**，它只决定排版尺寸。字体位图是按
            // 屏幕像素光栅化的，见 BuildFont。
            float scale = Screen.height > 0 ? Screen.height / DesignHeight : 1f;
            if (Mathf.Abs(scale - _scale) > 0.01f)
            {
                // 中途改分辨率的话，图集是按旧缩放光栅化的，字会重新发虚
                // （放大）或者变软（缩小）。游戏里改分辨率多数是启动时定下来的，
                // 所以先只报一声；真需要了再在图集这套上加"重建"。
                Main.Logger.Warning("[ImGui] 屏幕缩放从 " + _scale.ToString("0.###")
                    + " 变成 " + scale.ToString("0.###") + "，字体图集是按旧值建的，会发虚");
            }
            _scale = scale;

            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new NVec2(Screen.width / _scale, Screen.height / _scale);
            io.DisplayFramebufferScale = NVec2.One;
            io.DeltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.001f);

            // 输入：ImGui 要的是左上原点的坐标，Unity 的鼠标是左下原点。
            // 所以 y 要翻过来。
            Vector3 mouse = Input.mousePosition;
            io.AddMousePosEvent(mouse.x / _scale, (Screen.height - mouse.y) / _scale);
            io.AddMouseButtonEvent(0, Input.GetMouseButton(0));
            io.AddMouseButtonEvent(1, Input.GetMouseButton(1));
            io.AddMouseButtonEvent(2, Input.GetMouseButton(2));
            io.AddMouseWheelEvent(Input.mouseScrollDelta.x, Input.mouseScrollDelta.y);
            AddKeys(io);

            // 工作台关着的时候，搬过来的窗口跟着一起收掉 ——
            // uGUI 那边它们是工作台的子物体，一关界面就跟着消失；
            // 这里没有父子关系，只能自己盯着。
            if (Main.Overlay == null || !Main.Overlay.IsVisible)
            {
                _helpOpen = false;
                _versionOpen = false;
                // **顺手关掉不算"用户看过了"** —— uGUI 那边也是这样：关工作台不会把
                // 更新内容标记成已读，下次开还弹。所以把待回报的信号一起清掉。
                _versionClosed = false;
            }

            // 性能探针：把这一帧拆成「构建 UI / Render / 搬进 Unity」三段，再带上
            // 帧间隔 —— 光知道总帧率没用，得知道钱花在哪一段才能对着优化。
            // 跟 VerboseDiagnostics 走，和 [TickCost] / [时间轴] / [ImGui桥] 同一个开关。
            bool profileFrame = Main.Settings != null && Main.Settings.VerboseDiagnostics;
            long profileStart = profileFrame
                ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            // **原版粒子编辑器开着时，把我们这层压到底。**
            // 它是原生 uGUI 面板，而我们的画布在 32000 —— 不压的话它被工作台
            // 整层盖住，用户点「编辑」什么都看不到（实测反馈）。
            bool particleEditorOpen = false;
            try
            {
                scnEditor particleScene = scnEditor.instance;
                particleEditorOpen = particleScene != null
                    && particleScene.particleEditorContainer != null
                    && particleScene.particleEditorContainer.gameObject.activeSelf;
            }
            catch
            {
                particleEditorOpen = false;
            }
            SetOverlaySuppressedForParticleEditor(particleEditorOpen);
            // **uGUI 那层也要一起压。** 工作台的 uGUI 画布排在 30000，
            // 比原生粒子编辑器（order=1）高得多 —— 只压 ImGui 那层的话，
            // 它照样把面板盖在中间（实测「工作台消失一半、面板没出来」）。
            if (Main.Overlay != null)
            {
                Main.Overlay.SetCanvasSuppressed(particleEditorOpen);
            }

            ImGui.NewFrame();
            // 常驻面板先画，模态的文字窗口后画 —— 后者必须压在它上面。
            DrawPanels();
            // 悬浮球最后画：它工作台关着时才出现，和面板互斥。
            DrawFloatingBall();
            DrawWindows();
            long profileBuild = profileFrame
                ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            ImGui.Render();
            long profileRender = profileFrame
                ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            PushToCanvas();
            long profilePush = profileFrame
                ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            if (profileFrame)
            {
                probeBuildTicks += profileBuild - profileStart;
                probeRenderTicks += profileRender - profileBuild;
                probePushTicks += profilePush - profileRender;
                probeDeltaSum += Time.unscaledDeltaTime * 1000.0;
                probeSegFrames++;
                if (probeSegFrames >= 60)
                {
                    double perMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
                    double avgDelta = probeDeltaSum / probeSegFrames;
                    Main.Logger.Log("[ImGui帧] 60 帧均值：构建 "
                        + (probeBuildTicks / perMs).ToString("0.0") + " ms / Render "
                        + (probeRenderTicks / perMs).ToString("0.0") + " ms / 搬运 "
                        + (probePushTicks / perMs).ToString("0.0") + " ms ｜ 帧间隔 "
                        + avgDelta.ToString("0.0") + " ms（约 "
                        + (1000.0 / Mathf.Max((float)avgDelta, 0.001f)).ToString("0")
                        + " 帧）");
                    probeBuildTicks = 0L;
                    probeRenderTicks = 0L;
                    probePushTicks = 0L;
                    probeDeltaSum = 0.0;
                    probeSegFrames = 0;
                }
            }

            // 帧画完了才做副作用 —— 理由见 DrawHelpBody 里那段说明。
            if (_qqLinkClicked)
            {
                _qqLinkClicked = false;
                if (Main.Overlay != null)
                {
                    // 复制群号 + 打开加群页，和 uGUI 那扇点标题是同一件事。
                    Main.Overlay.OpenQqGroup();
                }
            }

            // 只要有搬过来的模态窗口开着，遮罩就亮着（点哪儿都被它接住）。
            bool modalOpen = _helpOpen || _versionOpen;
            if (_blocker != null && _blocker.gameObject.activeSelf != modalOpen)
            {
                _blocker.gameObject.SetActive(modalOpen);
            }

            // 遮罩盖**面板 ∪ 展开中的弹层** —— 弹层画在面板外面，只挡面板的话
            // 点它会穿到后面的工作台上。弹层矩形是 ImGui 自己报的，准。
            bool panelOn = Main.Overlay != null && Main.Overlay.IsVisible;
            SyncBlocker(_panelBlocker,
                panelOn ? Union(_inspector.Rect, _inspector.PopupRect)
                    : new Rect(0f, 0f, 0f, 0f), panelOn);
            SyncBlocker(_decoBlocker, _decorationList.Rect,
                panelOn && _decorationList.Rect.width > 0f);
            SyncBlocker(_mediaBlocker, _mediaPanel.Rect,
                panelOn && _mediaPanel.Rect.width > 0f);
            SyncBlocker(_settingsBlocker, _settingsInspector.Rect,
                panelOn && _settingsInspector.Rect.width > 0f);
            SyncBlocker(_headerBlocker, _header.Rect, panelOn && _header.Rect.width > 0f);
            SyncBlocker(_previewBlocker, _previewPanel.Rect,
                panelOn && _previewPanel.Rect.width > 0f);
            // **挡点击层要让开最上面那条缩放手柄。** 手柄是 uGUI 的、就贴在
            // 时间轴顶端那 9 像素上；挡住了它就没法拖时间轴高度（这是报过的问题）。
            // ImGui 窗口照旧盖满整行 —— ImGui 和 uGUI 是两套输入，互不影响。
            Rect headerBlocker = _timelineHeader.Rect;
            float handleInset = EditorTimelineView.ResizeHandleHeight;
            headerBlocker.y += handleInset;
            headerBlocker.height = Mathf.Max(0f, headerBlocker.height - handleInset);
            SyncBlocker(_timelineHeaderBlocker, headerBlocker,
                panelOn && headerBlocker.width > 0f && headerBlocker.height > 0f);
            // 事件面板那排也要一份：它原来是 uGUI 按钮、自己吃点击，换成 ImGui
            // 画之后不挡的话，点击会穿到下面的游戏编辑器去（见字段那段的说明）。
            SyncBlocker(_eventPaletteBlocker, _eventPalette.Rect,
                panelOn && _eventPalette.Rect.width > 0f && _eventPalette.Rect.height > 1f);
            SyncBlocker(_timelineBodyBlocker, _timelineBody.Rect,
                panelOn && _timelineBody.Rect.width > 0f);
            // 粒子编辑器是浮层：它自己盖住的那一块也要挡，否则拖窗口会带着
            // 后面的游戏画面一起动（实测就是这个"穿透"）。
            SyncBlocker(_particleBlocker, ParticleEditor.Rect,
                ParticleEditor.Visible && ParticleEditor.Rect.width > 0f);

            // **上下文还回去** —— 见方法开头那段说明。
            //
            // 放在这里是因为 ImGui 的活到上面就全干完了（NewFrame / Render /
            // PushToCanvas 都在里面），下面这些 SyncBlocker 只是 uGUI 的收尾，
            // 不碰 ImGui。
            if (previousContext != IntPtr.Zero)
            {
                ImGui.SetCurrentContext(previousContext);
            }
        }

        private static void AddKeys(ImGuiIOPtr io)
        {
            // 先只喂说明窗口用得上的几个键（滚动、关窗都走鼠标，
            // 键盘这边够了）。真接工作台的时候要补齐。
            io.AddKeyEvent(ImGuiKey.Enter, Input.GetKeyDown(KeyCode.Return));
            io.AddKeyEvent(ImGuiKey.Escape, Input.GetKeyDown(KeyCode.Escape));
            io.AddKeyEvent(ImGuiKey.Backspace, Input.GetKeyDown(KeyCode.Backspace));
            io.AddKeyEvent(ImGuiKey.LeftShift, Input.GetKey(KeyCode.LeftShift));
            // **修饰键要喂全。** 原来只喂了左 Shift，Ctrl 和 Alt 一个都没有 ——
            // 于是 ImGui 那边 `io.KeyCtrl` 永远是 false，所有"Ctrl+点击""Ctrl+拖"
            // 的分支从来没进过（表现是"Ctrl 点了只能单选""框选起不来"），
            // 而且查起来和 ImGui 毫不相干。
            io.AddKeyEvent(ImGuiKey.RightShift, Input.GetKey(KeyCode.RightShift));
            io.AddKeyEvent(ImGuiKey.LeftCtrl, Input.GetKey(KeyCode.LeftControl));
            io.AddKeyEvent(ImGuiKey.RightCtrl, Input.GetKey(KeyCode.RightControl));
            io.AddKeyEvent(ImGuiKey.LeftAlt, Input.GetKey(KeyCode.LeftAlt));
            io.AddKeyEvent(ImGuiKey.RightAlt, Input.GetKey(KeyCode.RightAlt));
            io.AddInputCharactersUTF8(Input.inputString ?? string.Empty);
        }

        // ---------------------------------------------------------------- 说明窗口

        /// <summary>卡片尺寸 / 位置：和 uGUI 那两扇文本窗口同一组数字（设计空间里的值）。</summary>
        private const float WindowWidth = 920f;
        private const float WindowHeight = 780f;

        private const ImGuiWindowFlags WindowFlags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings;

        // ---------------------------------------------------------------- 悬浮球

        /// <summary>悬浮球直径（设计空间像素）。</summary>
        private const float BallDiameter = 64f;
        /// <summary>初始位置离屏幕右缘的距离。</summary>
        private const float BallMargin = 28f;
        /// <summary>按下到松开之间移动超过这么多像素就算拖动，不算点击。</summary>
        private const float BallDragThreshold = 4f;

        private Texture2D _logoTexture;
        private IntPtr _logoTextureId;
        private bool _logoLoadTried;
        private NVec2 _ballCenter;
        private bool _ballPlaced;
        private NVec2 _ballPressPos;
        private NVec2 _ballPressCenter;
        private bool _ballMoved;

        /// <summary>悬浮球图标的分辨率（随包的 `Logo.rgba` 就是这个尺寸）。</summary>
        private const int LogoSize = 128;

        /// <summary>
        /// 把 mod 目录下的 `Logo.rgba` 读成纹理并登记给 ImGui。
        ///
        /// **为什么不是 PNG**：`Texture2D.LoadImage` 在 `UnityEngine.ImageConversionModule`
        /// 里，而那个程序集是 netstandard 2.1 编的，和本项目的 net48 引用冲突
        /// （CS1705）。所以图标随包发**原始 RGBA32 字节**，运行时
        /// `LoadRawTextureData` 直接吃 —— 不需要任何图片解码库，也不依赖那个模块。
        /// 文件按**图片自上而下**的顺序写，不要预翻 —— `LoadRawTextureData`
        /// 就按写入顺序解释，预先翻转会让球上下颠倒（实测踩过）。
        ///
        /// 读不到就返回零，调用方会退回一个实心圆 —— **入口不能因为一张图没读到
        /// 就消失**，那样用户连"有这个东西"都看不到。
        /// </summary>
        private IntPtr EnsureLogoTexture()
        {
            if (_logoLoadTried)
            {
                return _logoTextureId;
            }
            _logoLoadTried = true;
            try
            {
                string modPath = Main.Mod == null ? null : Main.Mod.Path;
                if (string.IsNullOrEmpty(modPath))
                {
                    return _logoTextureId;
                }
                string path = System.IO.Path.Combine(modPath, "Logo.rgba");
                if (!System.IO.File.Exists(path))
                {
                    Main.Logger.Log("[悬浮球] 找不到 Logo.rgba：" + path);
                    return _logoTextureId;
                }
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                int expected = LogoSize * LogoSize * 4;
                if (bytes.Length != expected)
                {
                    Main.Logger.Log("[悬浮球] Logo.rgba 大小不对："
                        + bytes.Length + "，应为 " + expected);
                    return _logoTextureId;
                }
                Texture2D texture = new Texture2D(LogoSize, LogoSize,
                    TextureFormat.RGBA32, false);
                texture.LoadRawTextureData(bytes);
                texture.Apply();
                texture.name = "ADOFAIEditorStudio.Logo";
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                _logoTexture = texture;
                _logoTextureId = RegisterTexture(texture);
            }
            catch (Exception exception)
            {
                Main.Logger.Log("[悬浮球] 读 Logo.rgba 失败：" + exception.Message);
            }
            return _logoTextureId;
        }

        private void ClampBallCenter(NVec2 display)
        {
            float radius = BallDiameter * 0.5f;
            _ballCenter = new NVec2(
                Mathf.Clamp(_ballCenter.X, radius, Mathf.Max(radius, display.X - radius)),
                Mathf.Clamp(_ballCenter.Y, radius, Mathf.Max(radius, display.Y - radius)));
        }

        /// <summary>
        /// 工作台关着时，编辑器里那颗悬浮球。
        ///
        /// **为什么要它**：原来靠快捷键打开，但实测有用户的那个键**根本进不了
        /// Unity**（笔记本 F 键默认走媒体键、或者被录屏 / 输入法 / 显卡 overlay
        /// 抢了全局热键），而且键盘方案没有任何可视入口 —— 用户连"这个功能存在"
        /// 都不知道。悬浮球看得见、点得到，也不跟任何按键冲突。
        ///
        /// 交互：**拖 = 挪位置**（钳在屏幕内），**点一下 = 打开工作台**
        /// （按下到松开没怎么移动才算点击）。工作台打开时不画。
        /// </summary>
        private void DrawFloatingBall()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null || overlay.IsVisible)
            {
                if (_ballBlocker != null && _ballBlocker.gameObject.activeSelf)
                {
                    _ballBlocker.gameObject.SetActive(false);
                }
                return;
            }
            if (!EditorBridge.IsEditorActive())
            {
                if (_ballBlocker != null && _ballBlocker.gameObject.activeSelf)
                {
                    _ballBlocker.gameObject.SetActive(false);
                }
                return;
            }
            // 游玩中不出现：原来 F8 那条路在这里会提示"先按 Esc 退出游玩"，
            // 悬浮球更简单 —— 直接不画，玩家根本点不到。
            if (EditorStudioOverlay.IsNativePlayModeRunning())
            {
                if (_ballBlocker != null && _ballBlocker.gameObject.activeSelf)
                {
                    _ballBlocker.gameObject.SetActive(false);
                }
                return;
            }

            IntPtr textureId = EnsureLogoTexture();
            ImGuiIOPtr io = ImGui.GetIO();
            NVec2 display = io.DisplaySize;
            if (!_ballPlaced)
            {
                _ballPlaced = true;
                _ballCenter = new NVec2(display.X - BallMargin - BallDiameter * 0.5f,
                    display.Y * 0.30f);
            }
            ClampBallCenter(display);

            NVec2 topLeft = new NVec2(_ballCenter.X - BallDiameter * 0.5f,
                _ballCenter.Y - BallDiameter * 0.5f);
            ImGui.SetNextWindowPos(topLeft, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new NVec2(BallDiameter, BallDiameter), ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0f, 0f, 0f, 0f));
            bool open = ImGui.Begin("##aes_floating_ball",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);
            if (!open)
            {
                ImGui.End();
                ImGui.PopStyleColor();
                ImGui.PopStyleVar(2);
                return;
            }

            NVec2 origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##ball", new NVec2(BallDiameter, BallDiameter));
            bool hovered = ImGui.IsItemHovered();

            if (ImGui.IsItemActivated())
            {
                _ballPressPos = io.MousePos;
                _ballPressCenter = _ballCenter;
                _ballMoved = false;
            }
            if (ImGui.IsItemActive())
            {
                NVec2 delta = new NVec2(io.MousePos.X - _ballPressPos.X,
                    io.MousePos.Y - _ballPressPos.Y);
                if (!_ballMoved
                    && Mathf.Abs(delta.X) + Mathf.Abs(delta.Y) > BallDragThreshold)
                {
                    _ballMoved = true;
                }
                if (_ballMoved)
                {
                    _ballCenter = new NVec2(_ballPressCenter.X + delta.X,
                        _ballPressCenter.Y + delta.Y);
                    ClampBallCenter(display);
                }
            }
            // **松手才算点击**：按下就开会把"想拖一下位置"变成"打开了工作台"。
            if (ImGui.IsItemDeactivated() && !_ballMoved)
            {
                overlay.OpenFromFloatingBall();
            }

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float radius = BallDiameter * 0.5f;
            NVec2 center = new NVec2(origin.X + radius, origin.Y + radius);
            if (textureId != IntPtr.Zero)
            {
                draw.AddImageRounded(textureId, origin,
                    new NVec2(origin.X + BallDiameter, origin.Y + BallDiameter),
                    new NVec2(0f, 0f), new NVec2(1f, 1f),
                    ImGui.GetColorU32(new NVec4(1f, 1f, 1f, hovered ? 1f : 0.92f)), radius);
            }
            else
            {
                draw.AddCircleFilled(center, radius,
                    ImGui.GetColorU32(ToVec4(Palette.Accent)), 32);
            }
            // **常驻描边。** 球是浮在游戏画面上的，没有轮廓时遇到亮背景会和背景
            // 糊在一起、看不出边界。平时用控件轮廓色（就是主题里按 3:1 选的那档），
            // 悬停换成强调色并加粗一点。
            float ringRadius = radius - 1f;
            draw.AddCircle(center, ringRadius,
                ImGui.GetColorU32(ToVec4(hovered
                    ? Palette.AccentSecondary
                    : UiTheme.Current.BorderStrong)),
                48, hovered ? 2.5f : 2f);
            if (hovered)
            {
                ImGui.SetTooltip("打开工作台");
            }

            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);

            // 点击遮挡：不然点悬浮球会先选中它下面的格子。
            SyncBlocker(_ballBlocker,
                new Rect(topLeft.X, topLeft.Y, BallDiameter, BallDiameter), true);
        }

        /// <summary>
        /// 画常驻面板（现在只有参数栏）。和文字窗口的区别：**不压暗、不模态**，
        /// 一直贴在自己那块地方；但一样要自己挡点击。
        ///
        /// 工作台关着的时候整个不画，并且**把 uGUI 那边让出来的面板还回去** ——
        /// 否则下次开工作台，右栏会缺一块。
        /// </summary>
        private void DrawPanels()
        {
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null || !overlay.IsVisible)
            {
                SyncBlocker(_panelBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_decoBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_mediaBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_settingsBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_headerBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_previewBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_timelineHeaderBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_eventPaletteBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_timelineBodyBlocker, new Rect(0f, 0f, 0f, 0f), false);
                SyncBlocker(_particleBlocker, new Rect(0f, 0f, 0f, 0f), false);
                if (overlay != null)
                {
                    overlay.SuppressInspector(false);
                    overlay.SuppressDecorations(false);
                    overlay.SuppressMedia(false);
                    overlay.SuppressSettings(false);
                    overlay.SuppressHeader(false);
                    overlay.SuppressPreviewChrome(false);
                    overlay.SuppressTimelineTitleRow(false);
                    overlay.SuppressTimelinePalette(false);
                    overlay.SuppressTimelineRuler(false);
                    overlay.SuppressTimelineBlocks(false);
                }
                return;
            }

            _header.Draw();
            _menuBar.Draw();
            _previewPanel.Draw();
            _timelineHeader.Draw();
            _eventPalette.Draw();
            _timelineBody.Draw();
            _inspector.Draw();
            _decorationList.Draw();
            _mediaPanel.Draw();
            _settingsInspector.Draw();
            // 粒子编辑器最后画：它是覆盖层，要压在其他面板上面。
            ParticleEditor.Draw(_inspector);
            // 接管这两个坑：把 uGUI 那两扇整个藏掉，不然会和 ImGui 版叠在一起。
            // 装饰物那扇只在它自己那一页压 —— 切到媒体页时 ImGui 不画东西，
            // 压住的话媒体页就是空的。
            overlay.SuppressInspector(true);
            overlay.SuppressDecorations(overlay.IsDecorationPane);
            overlay.SuppressMedia(overlay.IsMediaPane);
            overlay.SuppressSettings(overlay.IsSettingsPane);
            overlay.SuppressHeader(true);
            overlay.SuppressPreviewChrome(true);
            overlay.SuppressTimelineTitleRow(true);
            overlay.SuppressTimelinePalette(true);
            overlay.SuppressTimelineRuler(true);
            overlay.SuppressTimelineBlocks(true);
        }

        /// <summary>两个矩形的并集（空的那个直接返回另一个）。</summary>
        private static Rect Union(Rect a, Rect b)
        {
            if (a.width <= 0f || a.height <= 0f)
            {
                return b;
            }
            if (b.width <= 0f || b.height <= 0f)
            {
                return a;
            }
            float minX = Mathf.Min(a.x, b.x);
            float minY = Mathf.Min(a.y, b.y);
            float maxX = Mathf.Max(a.x + a.width, b.x + b.width);
            float maxY = Mathf.Max(a.y + a.height, b.y + b.height);
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 把挡点击的那块透明遮罩摆到面板的位置上。
        ///
        /// 设计坐标 → 屏幕像素要乘 `_scale`（画布是恒定像素的覆盖层），
        /// 而 y 要取负：画布是中心原点、Canvas 的左上角在 y=0 往下量。
        /// </summary>
        private void SyncBlocker(Image blocker, Rect designRect, bool active)
        {
            if (blocker == null)
            {
                return;
            }
            if (blocker.gameObject.activeSelf != active)
            {
                blocker.gameObject.SetActive(active);
            }
            if (!active)
            {
                return;
            }
            RectTransform rect = (RectTransform)blocker.transform;
            rect.anchoredPosition = new Vector2(designRect.x * _scale, -designRect.y * _scale);
            rect.sizeDelta = new Vector2(designRect.width * _scale, designRect.height * _scale);
        }

        /// <summary>
        /// 画这一帧所有已经搬过来的窗口。以后再加面板就从这里挂。
        /// </summary>
        private void DrawWindows()
        {
            DrawProjectCreateWindow();
            DrawConfirmSaveWindow();

            DrawTextWindow(ref _helpOpen, HelpTitle, _helpRows, false);

            // 版本说明窗口：它是被自己的标题栏 × 关掉的，工作台那边收不到通知，
            // 所以在这一帧记下来，等工作台来取（见 TakeVersionClosed）。
            bool wasOpen = _versionOpen;
            DrawTextWindow(ref _versionOpen, VersionTitle, _versionRows, true);
            if (wasOpen && !_versionOpen)
            {
                _versionClosed = true;
            }
        }

        /// <summary>
        /// 「时间轴布局还没保存」那扇窗：三个答案，保存 / 不保存 / 取消。
        ///
        /// **取消是默认语义**（连标题栏那个 × 也算取消）—— 关窗户不该等于
        /// 悄悄丢掉用户摆好的位置。
        /// </summary>
        private void DrawConfirmSaveWindow()
        {
            if (!_confirmSaveOpen)
            {
                return;
            }

            const float width = 560f;
            NVec2 display = ImGui.GetIO().DisplaySize;
            float height = Mathf.Max(230f, display.Y * 0.28f);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(
                new NVec2((display.X - width) * 0.5f, (display.Y - height) * 0.5f),
                ImGuiCond.Appearing);

            bool visible = true;
            if (ImGui.Begin("时间轴布局还没保存", ref visible, WindowFlags))
            {
                ImGui.Spacing();
                ImGui.TextWrapped("你在时间轴上摆的位置还没写进工程，要保存吗？");
                ImGui.Spacing();
                ImGui.TextDisabled("选「不保存」的话，这些位置下次打开就没了。");

                float footer = ImGui.GetWindowHeight() - 64f;
                if (footer > ImGui.GetCursorPosY())
                {
                    ImGui.SetCursorPosY(footer);
                }
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.Button("保存", new NVec2(140f, 40f)))
                {
                    _confirmSaveAnswer = 1;
                    _confirmSaveOpen = false;
                }
                ImGui.SameLine();
                if (ImGui.Button("不保存", new NVec2(140f, 40f)))
                {
                    _confirmSaveAnswer = 2;
                    _confirmSaveOpen = false;
                }
                ImGui.SameLine();
                if (ImGui.Button("取消", new NVec2(140f, 40f)))
                {
                    _confirmSaveAnswer = 3;
                    _confirmSaveOpen = false;
                }
            }
            ImGui.End();

            if (!visible && _confirmSaveOpen)
            {
                // 被标题栏的 × 关掉 —— 等同于「取消」，别把布局悄悄丢掉。
                _confirmSaveAnswer = 3;
                _confirmSaveOpen = false;
            }
        }

        /// <summary>
        /// 「创建工程」那一屏：给这张谱起个名，AES 才会开始记布局。
        ///
        /// **没有目录选择框** —— 目录跟谱面绑死（`<谱面目录>/<谱面名>_AES/`）。
        /// 开放自定义的话，下次打开这张谱就找不回来了。能改的只有工程名。
        ///
        /// 尺寸按屏幕比例给（大半屏），**不能用 `AlwaysAutoResize`** ——
        /// 那会把窗口收缩到内容大小，两个输入框一挤就没法看了。
        /// </summary>
        private void DrawProjectCreateWindow()
        {
            if (!_projectCreateOpen)
            {
                return;
            }

            NVec2 display = ImGui.GetIO().DisplaySize;
            float width = Math.Max(620f, display.X * 0.64f);
            float height = Math.Max(400f, display.Y * 0.64f);
            ImGui.SetNextWindowSize(new NVec2(width, height), ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(
                new NVec2((display.X - width) * 0.5f, (display.Y - height) * 0.5f),
                ImGuiCond.Appearing);

            bool visible = true;
            if (ImGui.Begin("创建工程", ref visible, WindowFlags))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "这张谱还没有工程。新建之后，你在时间轴上的排列会保存在这里。");
                ImGui.Spacing();
                ImGui.Spacing();

                ImGui.SeparatorText("工程名");
                ImGui.Spacing();
                ImGui.SetNextItemWidth(-1f);
                string typed = _projectName;
                if (ImGui.InputText("##projectName", ref typed, 120))
                {
                    _projectName = typed;
                }

                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.SeparatorText("保存位置");
                ImGui.Spacing();
                ImGui.TextWrapped(_projectPath);
                ImGui.Spacing();
                ImGui.TextDisabled(
                    "工程存放于谱面目录下的子文件夹，分享谱面时是否一并分发由你决定。");

                // 按钮推到窗口底部 —— 上面那些说明和输入框占满大半屏之后，
                // 按钮要是紧跟着文字飘在中间，读起来像还没写完。
                float footer = ImGui.GetWindowHeight() - 64f;
                if (footer > ImGui.GetCursorPosY())
                {
                    ImGui.SetCursorPosY(footer);
                }
                ImGui.Separator();
                ImGui.Spacing();

                // 名字空着就建不了 —— 建出来会是个没有文件名的路径。
                bool canCreate = !string.IsNullOrWhiteSpace(_projectName);
                if (!canCreate)
                {
                    ImGui.BeginDisabled();
                }
                if (ImGui.Button("新建工程", new NVec2(180f, 40f)))
                {
                    _projectCreateConfirmed = true;
                    _projectCreateOpen = false;
                }
                if (!canCreate)
                {
                    ImGui.EndDisabled();
                }

                ImGui.SameLine();
                if (ImGui.Button("取消", new NVec2(120f, 40f)))
                {
                    _projectCreateCancelled = true;
                    _projectCreateOpen = false;
                }
            }
            ImGui.End();

            // 被标题栏那个 × 关掉的，等同于取消。
            if (!visible && _projectCreateOpen)
            {
                _projectCreateCancelled = true;
                _projectCreateOpen = false;
            }
        }

        /// <summary>
        /// 一扇「文本窗口」：居中卡片 + 标题栏（自带 ×）+ 可滚的正文（+ 可选页脚链接）。
        ///
        /// 说明窗口和版本说明窗口共用这一套，差别只有标题、正文、要不要页脚。
        /// 抄成两份的话，表格、折行、滚动这几处踩过的坑得各修一遍，而且总会漏一处 ——
        /// uGUI 那边当初就是为了这个才把 `BuildTextWindow` 抽出来的。
        /// </summary>
        private void DrawTextWindow(ref bool open, string title, HelpRow[] rows, bool footer)
        {
            if (!open)
            {
                return;
            }

            NVec2 display = ImGui.GetIO().DisplaySize;
            // `Appearing`：只在它出现（第一次、或关掉再开）的时候摆一次位置，
            // 之后用户自己滚到哪儿就留在哪儿。
            ImGui.SetNextWindowSize(new NVec2(WindowWidth, WindowHeight), ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(
                new NVec2((display.X - WindowWidth) * 0.5f, (display.Y - WindowHeight) * 0.5f),
                ImGuiCond.Appearing);

            // 标题栏那条 × 是 ImGui 自己画的，点它就把 open 置回 false。
            bool visible = true;
            if (ImGui.Begin(title, ref visible, WindowFlags))
            {
                DrawRows(rows);
                if (footer)
                {
                    DrawFooterLink();
                }
            }
            ImGui.End();

            open = visible;
        }

        private void DrawRows(HelpRow[] rows)
        {
            int i = 0;
            while (i < rows.Length)
            {
                HelpRow row = rows[i];
                switch (row.Kind)
                {
                    case HelpKind.KeyValue:
                        // 连续几条快捷键合成一张带框的表格，画完跳到它后面那条。
                        i = DrawKeyTable(rows, i);
                        continue;
                    case HelpKind.Spacer:
                        ImGui.Spacing();
                        break;
                    case HelpKind.Heading:
                        // 小节标题。`SeparatorText` 自带那条分隔线，
                        // 正好是 uGUI 那边"标题 + 一条横线"的画法；标题本身用强调色。
                        if (i > 0)
                        {
                            ImGui.Spacing();
                        }
                        PushColor(ImGuiCol.Text, EmphasisColor);
                        ImGui.SeparatorText(row.Text);
                        ImGui.PopStyleColor();
                        break;
                    case HelpKind.Note:
                        PushColor(ImGuiCol.Text, MutedColor);
                        ImGui.TextUnformatted(row.Text);
                        ImGui.PopStyleColor();
                        break;
                    case HelpKind.Bullet:
                        DrawBullet(row);
                        break;
                    default:
                        DrawWrapped(row.Text);
                        break;
                }
                i++;
            }
        }

        /// <summary>正文末尾那条加群链接。两个窗口共用（说明窗口不带，版本说明带）。</summary>
        private void DrawFooterLink()
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            PushColor(ImGuiCol.Text, LinkColor);
            if (ImGui.Button(HelpFooter))
            {
                // **只记一个标记，动作等这一帧画完再做。**
                // 这一句夹在 Begin/End 之间，万一 OpenQqGroup 抛出去（比如
                // Application.OpenURL 在某些环境下会），这一帧就 Render 不掉，
                // 下一帧 NewFrame 直接弹原生断言框。
                _qqLinkClicked = true;
            }
            ImGui.PopStyleColor();
        }

        /// <summary>
        /// 把连续几条快捷键画成一张带框的表格，返回下一条**不是**快捷键的行号。
        ///
        /// 左列键名用强调色、右列说明用正文色；左列宽度按内容自适应 ——
        /// uGUI 那边是靠数空格凑对齐的，表格化之后不用凑了。
        /// </summary>
        private int DrawKeyTable(HelpRow[] rows, int start)
        {
            int end = start;
            while (end < rows.Length && rows[end].Kind == HelpKind.KeyValue)
            {
                end++;
            }

            const ImGuiTableFlags flags = ImGuiTableFlags.Borders
                | ImGuiTableFlags.SizingFixedFit
                | ImGuiTableFlags.PadOuterX;

            // `BeginTable` 返回 false（被裁掉之类的）时**仍然必须调 `EndTable`**，
            // 所以这里不能提前 return。
            if (ImGui.BeginTable("##helpkeys", 2, flags))
            {
                ImGui.TableSetupColumn("键", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("说明", ImGuiTableColumnFlags.WidthStretch);
                for (int i = start; i < end; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    PushColor(ImGuiCol.Text, EmphasisColor);
                    ImGui.TextUnformatted(rows[i].Key);
                    ImGui.PopStyleColor();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(rows[i].Text);
                }
            }
            ImGui.EndTable();
            return end;
        }

        /// <summary>
        /// 一颗圆点 + 折行的正文；条目若切出了功能名，功能名用强调色、接在同一行。
        ///
        /// `TextWrapped` 折的是"窗口宽度"，跟在圆点后面画就会折到最右边去 ——
        /// 所以先 `PushTextWrapPos` 把折行位置钉在当前光标处，画完再弹回去。
        /// </summary>
        private static void DrawBullet(HelpRow row)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            if (row.Key != null)
            {
                PushColor(ImGuiCol.Text, EmphasisColor);
                ImGui.TextUnformatted(row.Key);
                ImGui.PopStyleColor();
                // 接着画，不留缝：两段是同一句话。
                ImGui.SameLine(0f, 0f);
            }
            ImGui.TextUnformatted(row.Text);
            ImGui.PopTextWrapPos();
        }

        private static void DrawWrapped(string text)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }

        // 这几处颜色**不写死**，从工作台的 token 取：
        // 次要文字＝text.secondary，链接＝action.primary，强调文字＝text.emphasis。
        // 写死的话换主题时这里会漏改，屏幕上的读数就会比别处旧一档。
        private static NVec4 MutedColor
        {
            get { return ToVec4(Palette.TextSecondary); }
        }

        private static NVec4 EmphasisColor
        {
            get { return ToVec4(Palette.TextEmphasis); }
        }

        private static NVec4 LinkColor
        {
            get { return ToVec4(Palette.Accent); }
        }

        private static NVec4 ToVec4(Color color)
        {
            return new NVec4(color.r, color.g, color.b, color.a);
        }

        private static void PushColor(ImGuiCol slot, NVec4 color)
        {
            ImGui.PushStyleColor(slot, color);
        }

        /// <summary>
        /// ImGui 的坐标系按 1920x1080 排（见 Update 里的 `_scale`）。
        /// </summary>
        private const float DesignHeight = 1080f;

        // ---------------------------------------------------------------- 画到 Unity

        /// <summary>帧构建分段探针的累计值（见 Update）。定位完删。</summary>
        private static long probeBuildTicks;
        private static long probeRenderTicks;
        private static long probePushTicks;
        private static double probeDeltaSum;
        private static int probeSegFrames;

        /// <summary>按窗口数命令的探针打到第几帧了（定位完删）。</summary>
        private static int probeListFrames;

        /// <summary>桥的搬运探针累计值（见 PushToCanvas）。定位完删。</summary>
        private static int probeFrames;
        private static long probeVtxTotal;
        private static long probeCmdTotal;
        private static double probePushMsTotal;
        private static readonly System.Diagnostics.Stopwatch ProbePushWatch =
            new System.Diagnostics.Stopwatch();

        /// <summary>
        /// 把 ImGui 这一帧的绘制数据搬进 Unity。
        ///
        /// 一条 `ImDrawCmd` 对应一个 CanvasRenderer + 一个 Mesh —— 因为每条
        /// cmd 可能换贴图、换裁剪矩形，那不是一次绘制能表达的。
        /// </summary>
        private unsafe void PushToCanvas()
        {
            ImDrawDataPtr drawData = ImGui.GetDrawData();

            // 探针跟着 VerboseDiagnostics 走（开发者设置里开）：这一帧搬多少顶点、
            // 多少命令、整段搬运花多少毫秒，打上一个 60 帧窗口的均值。
            if (Main.Settings != null && Main.Settings.VerboseDiagnostics)
            {
                if (ProbePushWatch.IsRunning)
                {
                    ProbePushWatch.Stop();
                    probePushMsTotal += ProbePushWatch.Elapsed.TotalMilliseconds;
                }
                ProbePushWatch.Restart();
                int commands = 0;
                for (int i = 0; i < drawData.CmdListsCount; i++)
                {
                    commands += (int)drawData.CmdLists[i].CmdBuffer.Size;
                }
                probeVtxTotal += drawData.TotalVtxCount;
                probeCmdTotal += commands;
                probeFrames++;
                if (probeFrames >= 60)
                {
                    Main.Logger.Log("[ImGui桥] 60 帧均值：PushToCanvas "
                        + (probePushMsTotal / probeFrames).ToString("0.0") + " ms，顶点 "
                        + (probeVtxTotal / probeFrames) + " 个，绘制命令 "
                        + (probeCmdTotal / probeFrames) + " 条");
                    probeFrames = 0;
                    probeVtxTotal = 0;
                    probeCmdTotal = 0;
                    probePushMsTotal = 0.0;
                }
            }

            if (drawData.CmdListsCount == 0)
            {
                ClearCanvas();
                return;
            }

            // ImGui 是左上原点，Unity 的 ScreenSpaceOverlay 画布是中心原点。
            float halfWidth = -Screen.width * 0.5f;
            float halfHeight = Screen.height * 0.5f;

            int rendererIndex = 0;
            for (int i = 0; i < drawData.CmdListsCount; i++)
            {
                ImDrawListPtr list = drawData.CmdLists[i];
                int vertexCount = list.VtxBuffer.Size;
                int indexCount = list.IdxBuffer.Size;
                EnsureArrays(vertexCount, indexCount);

                // 临时探针（定位爆顶点）：按 draw list 数命令。**ImGui 每个 window
                // 就是一个 draw list**，所以这一眼能看出是谁在把命令/顶点撑大。
                //
                // 不能只记前几帧 —— 崩的是"拖动之后某一帧"，那个窗口盖不到。
                // 改成按异常大小触发：任一窗口顶点或命令数明显偏大就记一笔。
                if (vertexCount >= 20000 || list.CmdBuffer.Size >= 400)
                {
                    Main.Logger.Log("[ImGui命令] list" + i + " / 共 "
                        + drawData.CmdListsCount + " 个：命令 " + list.CmdBuffer.Size
                        + " 条，顶点 " + vertexCount + "（全帧 " + drawData.TotalVtxCount + "）");
                }

                ImDrawVert* vertices = (ImDrawVert*)list.VtxBuffer.Data.ToPointer();
                for (int v = 0; v < vertexCount; v++)
                {
                    _vertices[v] = new Vector3(
                        halfWidth + vertices[v].pos.X * _scale,
                        halfHeight - vertices[v].pos.Y * _scale,
                        0f);
                    _uvs[v] = new Vector2(vertices[v].uv.X, vertices[v].uv.Y);
                    uint packed = vertices[v].col;
                    _colors[v] = new Color32(
                        (byte)(packed & 0xFF),
                        (byte)((packed >> 8) & 0xFF),
                        (byte)((packed >> 16) & 0xFF),
                        (byte)((packed >> 24) & 0xFF));
                }

                ushort* sourceIndices = (ushort*)list.IdxBuffer.Data.ToPointer();
                for (int k = 0; k < indexCount; k++)
                {
                    _indices[k] = sourceIndices[k];
                }

                for (int c = 0; c < list.CmdBuffer.Size; c++)
                {
                    ImDrawCmdPtr command = list.CmdBuffer[c];
                    // 自定义回调（比如 ImGui 的图片绘制钩子）暂时跳过。
                    if (command.UserCallback != IntPtr.Zero)
                    {
                        continue;
                    }

                    int elementCount = (int)command.ElemCount;
                    if (elementCount <= 0)
                    {
                        continue;
                    }
                    if (_commandIndices.Length < elementCount)
                    {
                        _commandIndices = new int[elementCount];
                    }
                    // **这条命令实际用到哪一段顶点，要从它自己的索引里求。**
                    //
                    // 不能用 `VtxOffset` 分段：ImGui 只有在后端声明了
                    // `RendererHasVtxOffset` 时才会给它填非 0 值，我们这边**所有
                    // 命令的 VtxOffset 都是 0** —— 拿"下一条减这条"算长度会得到 0，
                    // 于是除每个窗口最后一条之外的命令全被跳过（实测就是
                    // 「大部分控件不见了，只剩零星几个」）。
                    //
                    // 正确做法：扫一遍这条命令的索引，取 min/max —— 那才是它真正
                    // 碰到的顶点。下面顶点只上传这一段、索引一并减去起点。
                    int vtxStart = int.MaxValue;
                    int vtxMax = int.MinValue;
                    for (int m = 0; m < elementCount; m++)
                    {
                        int index = _indices[(int)command.IdxOffset + m];
                        _commandIndices[m] = index;
                        if (index < vtxStart)
                        {
                            vtxStart = index;
                        }
                        if (index > vtxMax)
                        {
                            vtxMax = index;
                        }
                    }

                    int vtxCount = vtxMax - vtxStart + 1;
                    if (vtxStart < 0 || vtxCount <= 0
                        || vtxMax >= vertexCount)
                    {
                        rendererIndex++;
                        continue;
                    }

                    // 索引归零到这一段起点 —— 和下面 `SetVertices(_, vtxStart, _)`
                    // 必须同一个基准，否则顶点全抓错（上一版就栽在这）。
                    if (vtxStart != 0)
                    {
                        for (int m = 0; m < elementCount; m++)
                        {
                            _commandIndices[m] -= vtxStart;
                        }
                    }

                    EnsureRenderer(rendererIndex);
                    Mesh mesh = _meshes[rendererIndex];
                    mesh.Clear();
                    mesh.SetVertices(_vertices, vtxStart, vtxCount);
                    mesh.SetUVs(0, _uvs, vtxStart, vtxCount);
                    mesh.SetColors(_colors, vtxStart, vtxCount);
                    mesh.SetIndices(_commandIndices, 0, elementCount,
                        MeshTopology.Triangles, 0, false);

                    float clipX = halfWidth + command.ClipRect.X * _scale;
                    float clipY = halfHeight - command.ClipRect.W * _scale;
                    float clipWidth = (command.ClipRect.Z - command.ClipRect.X) * _scale;
                    float clipHeight = (command.ClipRect.W - command.ClipRect.Y) * _scale;

                    // 按这条 cmd 自己记的纹理 ID 挑贴图。我们现在会用到两种：
                    // 字体图集（汉字和图标字体）和游戏自己的那张图标图集
                    // （见 RegisterTexture）。**认不出来就退回字体图集** ——
                    // 退回去至少画出字来，比给一片空白强。
                    Texture texture;
                    if (!_textures.TryGetValue(command.TextureId, out texture) || texture == null)
                    {
                        texture = _fontTexture;
                    }

                    CanvasRenderer target = _renderers[rendererIndex];
                    target.EnableRectClipping(new Rect(clipX, clipY, clipWidth, clipHeight));
                    target.SetMaterial(_material, texture);
                    target.SetColor(Color.white);
                    target.SetMesh(mesh);
                    rendererIndex++;
                }
            }

            for (int n = rendererIndex; n < _renderers.Count; n++)
            {
                _renderers[n].Clear();
            }
            probeListFrames++;
        }

        private void EnsureArrays(int vertices, int indices)
        {
            if (_vertices.Length < vertices)
            {
                _vertices = new Vector3[vertices];
                _uvs = new Vector2[vertices];
                _colors = new Color32[vertices];
            }
            if (_indices.Length < indices)
            {
                _indices = new int[indices];
            }
        }

        private void EnsureRenderer(int index)
        {
            if (index < _renderers.Count)
            {
                return;
            }
            GameObject host = new GameObject("ImGui_Command_" + index, typeof(RectTransform));
            host.transform.SetParent(_canvas.transform, false);
            _renderers.Add(host.AddComponent<CanvasRenderer>());

            Mesh mesh = new Mesh { name = "ImGui_Mesh_" + index };
            // 每帧重建，标注成动态让 Unity 走更快的上传路径。
            mesh.MarkDynamic();
            _meshes.Add(mesh);
        }

        private void ClearCanvas()
        {
            for (int i = 0; i < _renderers.Count; i++)
            {
                _renderers[i].Clear();
            }
        }
    }
}
