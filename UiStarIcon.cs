using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 收藏用的星标图标。空心 = 未收藏，实心 = 已收藏。
    ///
    /// **程序化生成，不引入资源文件。** 打包走白名单（`pack-release.ps1`
    /// 只打 `Info.json` / DLL / LICENSE / 图标），多一张 png 就得改白名单，
    /// 漏改一次就是「装上去星星不见了」。同一个理由，形状也不从游戏里取 ——
    /// 那是别人的资源，本项目必须能独立发布。
    ///
    /// 画法照 `EditorPropertyPanel.GetCheckSprite()` 那一套：逐像素算到形状
    /// 轮廓的距离，用它做抗锯齿。纹理只建一次，之后走缓存。
    /// </summary>
    internal static class UiStarIcon
    {
        private const int Size = 32;

        /// <summary>
        /// 描边宽度（像素）。空心星靠它得到「一圈线」；实心星只借它做边缘抗锯齿。
        /// 2.2 是试出来的：再细在 16×16 的下拉行里会糊成一片灰。
        /// </summary>
        private const float StrokeHalf = 1.1f;

        private static Sprite hollowSprite;
        private static Sprite filledSprite;

        /// <summary>取一枚星标。<paramref name="filled"/> 为真取实心。</summary>
        public static Sprite Get(bool filled)
        {
            if (filled)
            {
                if (filledSprite == null)
                {
                    filledSprite = Build(true);
                }
                return filledSprite;
            }

            if (hollowSprite == null)
            {
                hollowSprite = Build(false);
            }
            return hollowSprite;
        }

        private static Sprite Build(bool filled)
        {
            Texture2D texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            // 五角星的十个顶点：外顶点与内顶点交替。半径按纹理边长的比例给，
            // 起始角取 -90°，这样尖角朝上 —— 朝下的星读起来像倒挂的。
            Vector2[] points = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float radius = (i % 2 == 0) ? 0.46f : 0.19f;
                float angle = -Mathf.PI * 0.5f + i * Mathf.PI / 5f;
                float cx = Size * 0.5f + Mathf.Cos(angle) * Size * radius;
                float cy = Size * 0.5f + Mathf.Sin(angle) * Size * radius;
                points[i] = new Vector2(cx, cy);
            }

            Color32[] pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float px = x + 0.5f;
                    // 纹理行是从下往上走的，而顶点是按从上往下的屏幕坐标算的，
                    // 所以这里把纵轴镜像一次，而不是在十个顶点上逐个调整。
                    float py = Size - (y + 0.5f);

                    float dist = DistanceToOutline(px, py, points);
                    float alpha;
                    if (filled)
                    {
                        // 实心：内部满、边界一像素过渡。
                        alpha = Mathf.Clamp01(StrokeHalf - dist + 0.5f);
                    }
                    else
                    {
                        // 空心：只留离轮廓 StrokeHalf 以内的一条带，
                        // 也就是「到中心线的距离小于 StrokeHalf」。
                        alpha = Mathf.Clamp01(StrokeHalf - Mathf.Abs(dist) + 0.5f);
                    }

                    pixels[y * Size + x] = new Color32(255, 255, 255,
                        (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, Size, Size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>
        /// 点到五角星轮廓的**有符号**距离：内部为负、外部为正。
        ///
        /// 空心星需要这个符号 —— 只用绝对值的话，内外两侧的像素会同时亮，
        /// 描边会变成内外两条线。判断内外用射线法（向 +x 打一条线，数穿过
        /// 轮廓边的次数，奇数次就在内部）。
        /// </summary>
        private static float DistanceToOutline(float px, float py, Vector2[] points)
        {
            float nearest = float.MaxValue;
            bool inside = false;
            int count = points.Length;

            for (int i = 0; i < count; i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[(i + 1) % count];

                float d = DistanceToSegment(px, py, a.x, a.y, b.x, b.y);
                if (d < nearest)
                {
                    nearest = d;
                }

                // 射线法：这条边是否跨过 py，且交点在 px 右侧。
                if (((a.y > py) != (b.y > py)))
                {
                    float crossX = a.x + (py - a.y) / (b.y - a.y) * (b.x - a.x);
                    if (crossX > px)
                    {
                        inside = !inside;
                    }
                }
            }

            return inside ? -nearest : nearest;
        }

        private static float DistanceToSegment(float px, float py,
            float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            float lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 0.0001f)
            {
                return Mathf.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            }

            float t = ((px - ax) * dx + (py - ay) * dy) / lengthSquared;
            t = Mathf.Clamp01(t);
            float cx = ax + t * dx;
            float cy = ay + t * dy;
            return Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }
    }
}
