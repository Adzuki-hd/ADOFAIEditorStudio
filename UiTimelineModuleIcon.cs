using System.Collections.Generic;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 时间轴「分组铅笔」那颗按钮的图标。
    ///
    /// **为什么不走 GCS.levelEventIcons：** 那是游戏的图标表，只有它自己那套
    /// 事件图标，没有"分组"这种东西。而项目有一条硬约束 —— 不把游戏资源
    /// 打进 zip；我们自己的图标可以随包，但更省事的是**运行时按矢量路径
    /// 画出来**：一份代码、零文件、要换形状只改顶点。
    ///
    /// 路径是用户给的 SVG（1024×1024 视框，一支笔）。这里把它展平成折线
    /// 顶点（圆弧已按 16 段采样），再用扫描线填充成一张贴图。
    ///
    /// 生成一次就缓存 —— 按钮要它的时候问一次，之后一直用同一张。
    /// </summary>
    internal static class UiTimelineModuleIcon
    {
        /// <summary>贴图边长。64 够用了：按钮上画到 18~20 像素，2 倍多一点不糊。</summary>
        private const int TextureSize = 64;

        /// <summary>SVG 路径的视框边长。</summary>
        private const float ViewBox = 1024f;

        private static Sprite cached;

        private struct P
        {
            internal float X;
            internal float Y;

            internal P(float x, float y)
            {
                X = x;
                Y = y;
            }
        }

        /// <summary>拿这支笔的精灵。第一次调用时生成，之后走缓存。</summary>
        internal static Sprite Get()
        {
            if (cached != null)
            {
                return cached;
            }

            List<P[]> polygons = BuildPath();
            Texture2D texture = Rasterize(polygons, TextureSize);
            cached = Sprite.Create(texture,
                new Rect(0f, 0f, TextureSize, TextureSize),
                new Vector2(0.5f, 0.5f),
                100f);
            cached.name = "TimelineModulePencil";
            return cached;
        }

        /// <summary>把 SVG 路径的两条子路径展平成顶点。</summary>
        private static List<P[]> BuildPath()
        {
            var body = new[]
            {
                new P(550.4000f, 292.4800f),
                new P(731.3920f, 473.4720f),
                new P(308.9920f, 895.8720f),
                new P(128.0000f, 895.8720f),
                new P(128.0000f, 714.8373f),
                new P(550.4000f, 292.4373f)
            };

            // 笔帽：圆弧按 16 段采样后得到的折线（见生成脚本）。
            var cap = new[]
            {
                new P(610.7307f, 232.1067f),
                new P(701.2267f, 141.6107f),
                new P(704.3289f, 138.7998f),
                new P(707.6917f, 136.3064f),
                new P(711.2826f, 134.1546f),
                new P(715.0671f, 132.3650f),
                new P(719.0088f, 130.9550f),
                new P(723.0697f, 129.9380f),
                new P(727.2107f, 129.3238f),
                new P(731.3920f, 129.1184f),
                new P(735.5733f, 129.3238f),
                new P(739.7143f, 129.9380f),
                new P(743.7752f, 130.9550f),
                new P(747.7169f, 132.3650f),
                new P(751.5014f, 134.1546f),
                new P(755.0923f, 136.3064f),
                new P(758.4551f, 138.7998f),
                new P(761.5573f, 141.6107f),
                new P(882.2613f, 262.2720f),
                new P(885.0768f, 265.3752f),
                new P(887.5744f, 268.7395f),
                new P(889.7299f, 272.3327f),
                new P(891.5225f, 276.1199f),
                new P(892.9351f, 280.0647f),
                new P(893.9539f, 284.1290f),
                new P(894.5692f, 288.2736f),
                new P(894.7749f, 292.4587f),
                new P(894.5692f, 296.6437f),
                new P(893.9539f, 300.7883f),
                new P(892.9351f, 304.8527f),
                new P(891.5225f, 308.7975f),
                new P(889.7299f, 312.5847f),
                new P(887.5744f, 316.1778f),
                new P(885.0768f, 319.5422f),
                new P(882.2613f, 322.6453f),
                new P(791.7227f, 413.1413f),
                new P(610.7307f, 232.1067f)
            };

            return new List<P[]> { body, cap };
        }

        /// <summary>
        /// 逐行扫描填充。两条子路径取**并集**（各自独立填充再 OR）——
        /// 它们本来就是两个分离的形状，不需要处理自相交。
        ///
        /// 抗锯齿用 4 倍纵向超采样：每行在实际采样位置上再细分 4 次，
        /// 覆盖率累加成 alpha。64 像素的图标这样够了，再多就是浪费。
        /// </summary>
        private static Texture2D Rasterize(List<P[]> polygons, int size)
        {
            int superSample = 4;
            float scale = size / ViewBox;
            // SVG 的 y 向下，Unity 纹理的 y 向上 —— 采样时翻一下。
            var coverage = new float[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int sub = 0; sub < superSample; sub++)
                {
                    // 当前采样行的 SVG 坐标（取像素中心，再按超采样细分）。
                    float sampleY = (y + (sub + 0.5f) / superSample) / scale;
                    // 翻转到 SVG 的向下坐标系。
                    float svgY = ViewBox - sampleY;

                    var crossings = new List<float>(16);
                    for (int poly = 0; poly < polygons.Count; poly++)
                    {
                        P[] points = polygons[poly];
                        crossings.Clear();
                        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
                        {
                            P a = points[i];
                            P b = points[j];
                            if ((a.Y > svgY) == (b.Y > svgY))
                            {
                                continue;
                            }
                            float t = (svgY - a.Y) / (b.Y - a.Y);
                            crossings.Add(a.X + t * (b.X - a.X));
                        }
                        if (crossings.Count < 2)
                        {
                            continue;
                        }
                        crossings.Sort();
                        for (int c = 0; c + 1 < crossings.Count; c += 2)
                        {
                            float x0 = crossings[c] * scale;
                            float x1 = crossings[c + 1] * scale;
                            if (x1 <= x0)
                            {
                                continue;
                            }
                            // 把这半开区间 [x0, x1) 内的像素各加一份覆盖率。
                            int from = Mathf.Max(0, Mathf.FloorToInt(x0));
                            int to = Mathf.Min(size - 1, Mathf.CeilToInt(x1) - 1);
                            for (int px = from; px <= to; px++)
                            {
                                float left = Mathf.Max(x0, px);
                                float right = Mathf.Min(x1, px + 1f);
                                float overlap = right - left;
                                if (overlap > 0f)
                                {
                                    coverage[y * size + px] += overlap;
                                }
                            }
                        }
                    }
                }
            }

            var pixels = new Color32[size * size];
            float perSample = 1f / superSample;
            for (int i = 0; i < coverage.Length; i++)
            {
                float alpha = Mathf.Clamp01(coverage[i] * perSample);
                // 图形是纯白 —— 按钮那边靠 `Image.color` 上色，白色才染得上。
                pixels[i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.Apply();
            return texture;
        }
    }
}
