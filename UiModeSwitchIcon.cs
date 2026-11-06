using System.Collections.Generic;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 顶栏模式切换按钮旁边的那枚图标 —— 用户给的 SVG（1024×1024 视框，
    /// 两枚上下相对的箭头：上排从左指向右、下排从右指向左，像「切换」）。
    ///
    /// 和 <see cref="UiTimelineModuleIcon"/> 同一套做法：把 SVG 路径展平成
    /// 折线顶点，再用扫描线填充成一张贴图。**不随包带图片文件** ——
    /// 一份代码、零资源，换形状只改顶点。
    ///
    /// 两条子路径是**分离的两个形状**（上下两排箭头），所以取并集即可，
    /// 不用处理自相交。
    /// </summary>
    internal static class UiModeSwitchIcon
    {
        /// <summary>贴图边长。按钮上画到 16~18 像素，64 够用。</summary>
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

        /// <summary>拿这枚图标的精灵。第一次调用时生成，之后走缓存。</summary>
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
            cached.name = "ModeSwitchArrows";
            return cached;
        }

        /// <summary>把 SVG 路径的两条子路径展平成顶点（圆弧按 6 段采样）。</summary>
        private static List<P[]> BuildPath()
        {
            var body = new[]
            {
                new P(714.4000f, 448.2100f),
                new P(877.1400f, 448.2100f),
                new P(880.7039f, 447.5606f),
                new P(883.8114f, 445.6988f),
                new P(886.0652f, 442.8627f),
                new P(887.1769f, 439.4149f),
                new P(887.0045f, 435.7964f),
                new P(885.5700f, 432.4700f),
                new P(771.1800f, 260.4700f),
                new P(770.1421f, 259.1604f),
                new P(768.9028f, 258.0395f),
                new P(767.4959f, 257.1377f),
                new P(765.9598f, 256.4798f),
                new P(764.3365f, 256.0836f),
                new P(762.6700f, 255.9600f),
                new P(726.1600f, 256.2400f),
                new P(720.4862f, 257.0278f),
                new P(715.2068f, 259.2505f),
                new P(710.6781f, 262.7581f),
                new P(707.2057f, 267.3138f),
                new P(705.0239f, 272.6103f),
                new P(704.2800f, 278.2900f),
                new P(704.2800f, 338.5500f),
                new P(703.6445f, 343.3770f),
                new P(701.7814f, 347.8750f),
                new P(698.8175f, 351.7375f),
                new P(694.9550f, 354.7014f),
                new P(690.4570f, 356.5645f),
                new P(685.6300f, 357.2000f),
                new P(137.6300f, 357.2000f),
                new P(135.0082f, 357.5452f),
                new P(132.5650f, 358.5572f),
                new P(130.4670f, 360.1670f),
                new P(128.8572f, 362.2650f),
                new P(127.8452f, 364.7082f),
                new P(127.5000f, 367.3300f),
                new P(127.5000f, 438.0400f),
                new P(127.8472f, 440.6609f),
                new P(128.8602f, 443.1029f),
                new P(130.4699f, 445.2001f),
                new P(132.5671f, 446.8098f),
                new P(135.0091f, 447.8228f),
                new P(137.6300f, 448.1700f),
            };

            var cap = new[]
            {
                new P(885.7500f, 576.0000f),
                new P(146.7500f, 576.0000f),
                new P(144.4041f, 576.2780f),
                new P(142.1907f, 577.1032f),
                new P(140.2355f, 578.4289f),
                new P(138.6496f, 580.1797f),
                new P(137.5231f, 582.2560f),
                new P(136.9200f, 584.5400f),
                new P(136.7393f, 585.9033f),
                new P(136.7407f, 587.2786f),
                new P(136.9243f, 588.6416f),
                new P(137.2868f, 589.9682f),
                new P(137.8219f, 591.2351f),
                new P(138.5200f, 592.4200f),
                new P(249.4600f, 758.1100f),
                new P(251.7128f, 760.9437f),
                new P(254.3980f, 763.3716f),
                new P(257.4435f, 765.3286f),
                new P(260.7677f, 766.7621f),
                new P(264.2813f, 767.6337f),
                new P(267.8900f, 767.9200f),
                new P(297.2200f, 767.8500f),
                new P(302.9363f, 767.0876f),
                new P(308.2610f, 764.8728f),
                new P(312.8318f, 761.3565f),
                new P(316.3378f, 756.7777f),
                new P(318.5405f, 751.4480f),
                new P(319.2900f, 745.7300f),
                new P(319.2900f, 685.0800f),
                new P(319.9036f, 680.4012f),
                new P(321.7076f, 676.0408f),
                new P(324.5790f, 672.2961f),
                new P(328.3221f, 669.4226f),
                new P(332.6815f, 667.6162f),
                new P(337.3600f, 667.0000f),
                new P(885.7500f, 667.0000f),
                new P(888.3201f, 666.6616f),
                new P(890.7150f, 665.6696f),
                new P(892.7716f, 664.0916f),
                new P(894.3496f, 662.0350f),
                new P(895.3416f, 659.6401f),
                new P(895.6800f, 657.0700f),
                new P(895.6800f, 586.0000f),
                new P(895.3547f, 583.4172f),
                new P(894.3700f, 581.0075f),
                new P(892.7935f, 578.9361f),
                new P(890.7332f, 577.3450f),
                new P(888.3304f, 576.3434f),
                new P(885.7500f, 576.0000f),
            };
            return new List<P[]> { body, cap };
        }

        /// <summary>
        /// 逐行扫描填充。两条子路径取**并集**（各自独立填充再累加）——
        /// 它们本来就是两个分离的形状，不需要处理自相交。
        ///
        /// 抗锯齿用 4 倍纵向超采样：每行在实际采样位置上再细分 4 次，
        /// 覆盖率累加成 alpha。
        /// </summary>
        private static Texture2D Rasterize(List<P[]> polygons, int size)
        {
            const int superSample = 4;
            float scale = size / ViewBox;
            // SVG 的 y 向下，Unity 纹理的 y 向上 —— 采样时翻一下。
            var coverage = new float[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int sub = 0; sub < superSample; sub++)
                {
                    float sampleY = (y + (sub + 0.5f) / superSample) / scale;
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
