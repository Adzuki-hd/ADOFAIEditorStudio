using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ImGuiNET;
using UnityEngine;
using UnityEngine.UI;
using NVec2 = System.Numerics.Vector2;
using static ADOFAIEditorStudio.EditorImGuiDraw;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// ImGui 这边取"游戏原版图标"的地方。
    ///
    /// **只借引用，不拷像素、不落盘、不进包。**
    ///
    /// 游戏的 `Sprite` 自带 `texture` 和 `uv`，而 ImGui 画图要的正好就是
    /// "一张贴图 + 一块 UV 矩形"（`AddImage`），所以把游戏那张图集直接登记给
    /// ImGui 就行 —— 连一份内存拷贝都不用做。图标始终留在游戏的资源里，
    /// 我们的 zip 里一个字节的原版美术都不含；游戏一关，引用自然失效。
    ///
    /// 来源和 uGUI 那版**完全一致**（挨着放的两个人必须用同一套图标，
    /// 不然一眼看出是两拨人做的）：
    ///   - 装饰物类型图标：`GCS.levelEventIcons`，按 `LevelEventType` 取
    ///   - 睁眼/闭眼、开锁/闭锁：`propertyControlDecorationsList` 上的原生字段
    ///   - 删除：原版删除按钮**子节点**上那张图（不是按钮自己的底图 —— 那是
    ///     一个圆角矩形，拿来当图标会贴出一个实心小方块）
    ///
    /// **拿不到是本分，不是意外。** 工作台可能在编辑器原生 UI 就绪之前就打开了，
    /// 那时这些引用全是空的。所以每个入口都返回 false，让调用方自己退回
    /// **我们自己画的那张**（见 TryGetDrawn），再退回文字。
    /// 两级兜底见 EditorImGuiDecorationList 里的 DrawIcon。
    /// </summary>
    internal static class EditorImGuiIcons
    {
        /// <summary>
        /// ImGui 画一张图要的两样东西。
        ///
        /// UV 用 `System.Numerics.Vector2`（就是 ImGui 那套）而不是 Unity 的：
        /// 这个结构体唯一的去处就是 `ImDrawList.AddImage`，在源头就统一省得
        /// 每处调用都得转一次。
        /// </summary>
        internal struct Icon
        {
            internal System.IntPtr TextureId;
            internal NVec2 UvMin;
            internal NVec2 UvMax;
            /// <summary>
            /// 宽 / 高。**画的时候必须保持它。**
            ///
            /// 原版图标不一定是正方的（垃圾桶往往是竖长的）。按正方去画就是横向
            /// 拉伸，看起来"瘦"或者"胖"—— 那不是图标画得不好，是被我们拉变形了。
            /// </summary>
            internal float Aspect;
        }

        private static scnEditor Editor
        {
            get { return scnEditor.instance; }
        }

        // ------------------------------------------------------------ 类型图标

        /// <summary>四个装饰物类型图标（图片/文本/粒子/对象）。取不到返回 false。</summary>
        internal static bool TryGetType(LevelEventType type, out Icon icon)
        {
            icon = default(Icon);
            if (GCS.levelEventIcons == null)
            {
                return false;
            }
            Sprite sprite = null;
            GCS.levelEventIcons.TryGetValue(type, out sprite);
            return FromSprite(sprite, out icon);
        }

        // ------------------------------------------------------------ 行上的三个

        internal static bool TryGetEye(bool open, out Icon icon)
        {
            PropertyControl_DecorationsList native = Native();
            Sprite sprite = native == null
                ? null
                : (open ? native.eyeOpenSprite : native.eyeClosedSprite);
            return FromSprite(sprite, out icon);
        }

        internal static bool TryGetLock(bool closed, out Icon icon)
        {
            PropertyControl_DecorationsList native = Native();
            Sprite sprite = native == null
                ? null
                : (closed ? native.lockClosedSprite : native.lockOpenSprite);
            return FromSprite(sprite, out icon);
        }

        internal static bool TryGetDelete(out Icon icon)
        {
            return FromSprite(ResolveRemoveSprite(Native()), out icon);
        }

        /// <summary>
        /// 任意一个游戏精灵 → ImGui 要的「贴图 + UV」。
        ///
        /// **给"从别处拿到的图标"用** —— 目前只有预览工具栏那排车道筛选按钮
        /// （图标由时间轴按车道给，见 `EditorTimelineView.GetLaneIcon`）。
        /// 那批不适合塞进上面那组固定入口，所以把转换这一步单独开出来。
        /// </summary>
        internal static bool TryFromSprite(Sprite sprite, out Icon icon)
        {
            return FromSprite(sprite, out icon);
        }

        // ------------------------------------------------------------ 我们自己画的

        /// <summary>
        /// 兜底那一级用哪个图标。
        ///
        /// `None` 是"这一类我们没有自己画的" —— 比如四个装饰物类型图标，
        /// uGUI 那版也是纯取原版，我们自己没画过，那就直接退到文字。
        /// </summary>
        internal enum Drawn
        {
            None = 0,
            EyeOpen,
            EyeClosed,
            LockOpen,
            LockClosed,
            Delete
        }

        private static readonly Texture2D[] DrawnCache = new Texture2D[6];

        /// <summary>
        /// 取"我们自己画的"图标。**用的就是 uGUI 那版同一份像素图**
        /// （`EditorDecorationList` 里的 `EyeGlyph` / `LockGlyph` / `CrossGlyph`），
        /// 不另画一份 —— 两个界面挨着放，图标不一样一眼就看得出来。
        ///
        /// 贴图是第一次用到时才建的（12x12，一次而已），之后一直复用。
        /// </summary>
        internal static bool TryGetDrawn(Drawn which, out Icon icon)
        {
            icon = default(Icon);
            int slot = (int)which;
            if (slot <= 0 || slot >= DrawnCache.Length)
            {
                return false;
            }

            Texture2D texture = DrawnCache[slot];
            // Unity 的 `==` 被重载过，贴图被 Destroy 之后这里会返回 true，
            // 所以场景重启后能自动重建（见 ReleaseDrawn）。
            if (texture == null)
            {
                texture = BuildDrawn(which);
                if (texture == null)
                {
                    return false;
                }
                DrawnCache[slot] = texture;
            }

            System.IntPtr id = EditorImGuiBridge.RegisterTexture(texture);
            if (id == System.IntPtr.Zero)
            {
                return false;
            }
            icon.TextureId = id;
            // 纵轴换头，理由和游戏贴图那条一样（见 FromSprite）：ImGui 的 v 向下、
            // Unity 贴图的 v 向上。位图在 BuildGlyphTexture 里已经翻过一次，
            // 所以这里给的就是"上 → 下"。
            icon.UvMin = new NVec2(0f, 1f);
            icon.UvMax = new NVec2(1f, 0f);
            icon.Aspect = 1f;   // 自绘那几张是 12x12，正方
            return true;
        }

        /// <summary>
        /// 把我们建的那几张 12x12 贴图清掉。**只清我们自己的** —— 游戏那批贴图
        /// 是借来的，一个都不能动（见 EditorImGuiBridge 的 `_textures`）。
        /// </summary>
        internal static void ReleaseDrawn()
        {
            for (int i = 0; i < DrawnCache.Length; i++)
            {
                if (DrawnCache[i] != null)
                {
                    UnityEngine.Object.Destroy(DrawnCache[i]);
                    DrawnCache[i] = null;
                }
            }
        }

        /// <summary>
        /// 在给定的矩形里居中画一个图标。**两级兜底：游戏原版图标 → 我们自己画的。**
        ///
        /// 两级都拿不到才写字（`text` 填那个字，比如"隐"）。每级都只是"拿不到就
        /// 往下退"，所以缺哪一级都只影响观感，绝不会画出豆腐块或者一片空白。
        ///
        /// **两条 UI 路径共用这一份**（装饰物列表的行尾按钮、参数栏那个事件生效
        /// 开关）。各写一份的话，大小、居中方式、兜底文字迟早走散 —— 而两边在
        /// 同一个界面上挨着放，不一样一眼就看得出来。
        ///
        /// 图标按矩形的**短边**取正方形居中，不拉满整个矩形 —— 按钮可能是长条的，
        /// 拉满会把图标压扁。`preferredSize` 是"图标最大画多大"，通常比按钮小一圈。
        /// </summary>
        internal static void DrawIcon(ImDrawListPtr draw, NVec2 min, NVec2 max,
            float preferredSize, bool hasGame, Icon game, Drawn drawn, string text, Color tint)
        {
            uint color = ImGui.GetColorU32(ToVec4(tint));
            float width = max.X - min.X;
            float height = max.Y - min.Y;
            float box = Mathf.Min(preferredSize, Mathf.Min(width, height));

            // 一级：游戏原版图标。
            if (hasGame)
            {
                DrawSprite(draw, min, width, height, box,
                    game.TextureId, game.UvMin, game.UvMax, game.Aspect, color);
                return;
            }

            // 二级：我们自己画的那张。**和 uGUI 那版是同一份像素图**
            // （EditorDecorationList 里的 EyeGlyph / LockGlyph / CrossGlyph），
            // 不是另画一份 —— 两边挨着放，图标不一样一眼就看得出来。
            Icon own;
            if (TryGetDrawn(drawn, out own))
            {
                DrawSprite(draw, min, width, height, box,
                    own.TextureId, own.UvMin, own.UvMax, own.Aspect, color);
                return;
            }

            // 都没有才写字。**按图标那个字号画** —— 用默认字号会比旁边的图标
            // 大一圈，两者交替出现时跳得很明显。
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            EditorImGuiBridge bridge = EditorImGuiBridge.Instance;
            ImFontPtr font = bridge == null ? default(ImFontPtr) : bridge.UiFont;
            if (bridge == null || !bridge.HasUiFont)
            {
                // 连中文字体都没有（开发机上没带 UiFont.ttf），退回默认字号。
                NVec2 extent = ImGui.CalcTextSize(text);
                draw.AddText(new NVec2(min.X + (width - extent.X) * 0.5f,
                                       min.Y + (height - extent.Y) * 0.5f),
                    color, text);
                return;
            }

            // CalcTextSizeA 是"按指定字号量这个字体里的这段字"——
            // 不能用 ImGui.CalcTextSize，那个只认当前字号。
            NVec2 size = font.CalcTextSizeA(box, float.MaxValue, 0f, text);
            draw.AddText(font, box,
                new NVec2(min.X + (width - size.X) * 0.5f,
                          min.Y + (height - size.Y) * 0.5f),
                color, text);
        }

        /// <summary>
        /// 把一张精灵居中画进按钮里，**保持原始长宽比**。
        ///
        /// 不保比例就是纵向或横向拉伸 —— 垃圾桶那种竖长的图标会被压成"瘦"的，
        /// 看着像图标本身画得不好，其实是我们拉变形的。
        /// </summary>
        internal static void DrawSprite(ImDrawListPtr draw, NVec2 min,
            float width, float height, float box,
            System.IntPtr textureId, NVec2 uvMin, NVec2 uvMax, float aspect, uint color)
        {
            if (aspect <= 0f)
            {
                aspect = 1f;
            }
            float drawW = aspect >= 1f ? box : box * aspect;
            float drawH = aspect >= 1f ? box / aspect : box;
            float x0 = min.X + (width - drawW) * 0.5f;
            float y0 = min.Y + (height - drawH) * 0.5f;
            draw.AddImage(textureId, new NVec2(x0, y0),
                new NVec2(x0 + drawW, y0 + drawH), uvMin, uvMax, color);
        }

        private static Texture2D BuildDrawn(Drawn which)
        {
            switch (which)
            {
                case Drawn.EyeOpen:
                    return EditorDecorationList.BuildGlyphTexture(
                        "AesIconEyeOpen", EditorDecorationList.EyeGlyph(true));
                case Drawn.EyeClosed:
                    return EditorDecorationList.BuildGlyphTexture(
                        "AesIconEyeClosed", EditorDecorationList.EyeGlyph(false));
                case Drawn.LockOpen:
                    return EditorDecorationList.BuildGlyphTexture(
                        "AesIconLockOpen", EditorDecorationList.LockGlyph(false));
                case Drawn.LockClosed:
                    return EditorDecorationList.BuildGlyphTexture(
                        "AesIconLockClosed", EditorDecorationList.LockGlyph(true));
                case Drawn.Delete:
                    return EditorDecorationList.BuildGlyphTexture(
                        "AesIconDelete", EditorDecorationList.CrossGlyph());
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------ 内部

        private static PropertyControl_DecorationsList Native()
        {
            scnEditor editor = Editor;
            return editor == null ? null : editor.propertyControlDecorationsList;
        }

        /// <summary>
        /// 找原版「删除」按钮上的图标。翻不到返回 null，让调用方退回我们自己画的。
        ///
        /// 和 uGUI 那版的 ResolveRemoveSprite 是同一套逻辑，**不能取
        /// removeButton.targetGraphic 的精灵**（见类注释）。
        /// </summary>
        private static Sprite ResolveRemoveSprite(PropertyControl_DecorationsList native)
        {
            if (native == null || native.removeButton == null)
            {
                return null;
            }
            Transform button = native.removeButton.transform;
            for (int i = 0; i < button.childCount; i++)
            {
                Image child = button.GetChild(i).GetComponent<Image>();
                if (child != null && child.sprite != null)
                {
                    return child.sprite;
                }
            }
            return null;
        }

        /// <summary>
        /// `Sprite` → ImGui 要的「贴图 + UV 矩形」。
        ///
        /// UV 取 `sprite.uv` 的包围盒，不用 `textureRect` 换算：前者是精灵真正
        /// 用的那几块 UV（打包过的精灵也能对上），后者在图集做了紧凑打包时
        /// 会偏。代价是**旋转打包的精灵对不上** —— 那种 UV 不是一个矩形，
        /// 而 `AddImage` 只收矩形。UI 图标极少这么打，真碰上了会画歪，
        /// 不是画错位置就是画到邻居，一眼能看出来。
        /// </summary>
        private static bool FromSprite(Sprite sprite, out Icon icon)
        {
            icon = default(Icon);
            if (sprite == null)
            {
                return false;
            }
            Texture2D texture = sprite.texture;
            if (texture == null)
            {
                return false;
            }
            Vector2[] uv = sprite.uv;
            if (uv == null || uv.Length == 0)
            {
                return false;
            }

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < uv.Length; i++)
            {
                if (uv[i].x < minX) { minX = uv[i].x; }
                if (uv[i].x > maxX) { maxX = uv[i].x; }
                if (uv[i].y < minY) { minY = uv[i].y; }
                if (uv[i].y > maxY) { maxY = uv[i].y; }
            }

            System.IntPtr id = EditorImGuiBridge.RegisterTexture(texture);
            if (id == System.IntPtr.Zero)
            {
                return false;
            }

            icon.TextureId = id;
            // **纵轴要换头。**
            //
            // ImGui 的 v=0 在矩形的**上**边，Unity 贴图的 v=0 在**下**边，两边反着。
            // 所以把 sprite 的上下端点对调：矩形顶边采样 sprite 的顶（maxY），
            // 底边采样 sprite 的底（minY）。
            //
            // 字体那条路不用翻 —— 图集上传时 `LoadRawTextureData` 按底层布局拷，
            // 正好把这次反转抵消掉了（见 EditorImGuiBridge 里那段说明）。
            // 游戏贴图是原样上传的，没有那次抵消，所以必须在这里翻。
            icon.UvMin = new NVec2(minX, maxY);
            icon.UvMax = new NVec2(maxX, minY);
            // 长宽比取**像素矩形**那个，不是 UV 矩形 —— UV 是归一化过的，
            // 图集是长方形的话两者算出来的比例不一样。
            Rect rect = sprite.textureRect;
            icon.Aspect = rect.height > 0.5f ? rect.width / rect.height : 1f;
            return true;
        }
    }
}
