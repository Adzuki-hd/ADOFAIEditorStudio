using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 下拉用的实心三角图标。**只做一个朝下的形状，收起态靠旋转实现。**
    ///
    /// 画两个图标（下三角 / 左三角）是另一种做法，但那样切换时是「换一张图」，
    /// 中间没有过渡；旋转则天然能接弹性缓动，读起来是一个动作而不是一次跳变。
    ///
    /// 形状照用户给的那枚 SVG：等边三角、尖朝下、带圆角。圆角不是装饰 ——
    /// 直角尖在 11px 的行里会糊成一个点，圆角才保得住轮廓。
    ///
    /// **程序化生成，不引入资源文件。** 打包走白名单（`pack-release.ps1`
    /// 只打 `Info.json` / DLL / LICENSE / 图标），多一张 png 就得改白名单，
    /// 漏改一次就是「装上去箭头不见了」。
    /// </summary>
    internal static class UiTriangleIcon
    {
        private const int Size = 32;

        /// <summary>
        /// 圆角半径（纹理像素）。
        ///
        /// 4.0 在 32px 纹理上是「角明显是圆的、三条边仍读得出是直线」的那一档：
        /// 底角处每条边被切掉约 8.8px（边长的 29%），中间还留 13px 直线。
        /// 再大就整体趋近圆饼。
        /// </summary>
        private const float CornerRadius = 4f;

        private static Sprite sprite;

        /// <summary>取三角图标（尖朝下）。</summary>
        public static Sprite Get()
        {
            if (sprite == null)
            {
                sprite = Build();
            }
            return sprite;
        }

        private static Sprite Build()
        {
            Texture2D texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            // 三个顶点：上边平、下边尖。
            //
            // **比例照着用户给的 SVG 量。** 那枚图形的包围盒是 870×503，
            // 宽高比约 1.73 —— 是个**扁宽**的三角，不是等边的。
            //
            // **纹理要尽量占满。** 显示尺寸只有 18px 上下，纹理里每浪费
            // 一个像素，屏幕上就少半个。之前用 0.44/0.62 的系数，可见高度
            // 只剩 7.5px —— 和原来那个 `▾` 字符一样小，用户看截图说
            // 「没变化」是对的。两轮改动都栽在同一个算术上。
            //
            // 留白只留旋转需要的：这个图标绕中心转到 -90° 时，**宽度**变成
            // 高度。所以横向最多到 0.48 × Size（宽 30.7），转过来后占 30.7
            // 高，仍小于 32，不会被方形边界切掉。
            Vector2[] points = new Vector2[3];
            float halfBase = Size * 0.48f;        // 宽 30.7，占 96%
            float height = halfBase * 2f / 1.73f; // 按 SVG 的 1.73 反推高度 ≈ 17.7
            float top = (Size - height) * 0.5f;
            points[0] = new Vector2(Size * 0.5f - halfBase, top);
            points[1] = new Vector2(Size * 0.5f + halfBase, top);
            points[2] = new Vector2(Size * 0.5f, top + height);

            // **圆角 = 内缩三角再按 R 外扩，不是把整个形状内缩 R。**
            //
            // 之前写的是 `-(dist + R)`，那等于三条边一起向内挪 R ——
            // 整个三角形缩小一圈，而不是只圆角。这个三角的内切半径只有 7px，
            // R 取 6 时内缩后只剩 1px，整枚图标塌成一个点。
            //
            // 正确做法（Minkowski 和）：先把三角按 R 内缩、得到一个小三角，
            // 再对它求距离后减去 R，等价于把小三角朝外扩回去。三条边回到
            // 原位，只有三个尖角变成半径 R 的圆弧。
            //
            // **内缩量和外扩量必须是同一个数。** 这里先把它夹到内切半径的
            // 0.7 倍以内 —— 之后内缩、外扩都用这个夹过的值，否则外扩会比
            // 内缩多、整个图形被撑大。
            //
            // 上限取 0.7 而不是更小：这个三角的内切半径约 7，0.7 倍即 4.9，
            // 屏幕上约 3.4px 的圆角，能清楚看出「角是圆的」；再大整枚图标
            // 就开始像圆饼了。
            float inradius = InradiusOf(points);
            float radius = Mathf.Clamp(CornerRadius, 0f, inradius * 0.7f);
            // 内缩只算一次 —— 放进逐像素循环里会白跑 1024 遍。
            Vector2[] inset = InsetTriangle(points, radius, inradius);

            Color32[] pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float px = x + 0.5f;
                    // 纹理行是从下往上走的，而顶点是按从上往下的坐标描述的，
                    // 所以这里把纵轴镜像一次，而不是在三个顶点上逐个调整。
                    float py = Size - (y + 0.5f);

                    float roundedDist = SignedDistanceToOutline(px, py, inset) - radius;
                    float alpha = Mathf.Clamp01(-roundedDist + 0.5f);
                    pixels[y * Size + x] = new Color32(255, 255, 255,
                        (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite result = Sprite.Create(
                texture,
                new Rect(0f, 0f, Size, Size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            result.hideFlags = HideFlags.HideAndDontSave;
            return result;
        }

        /// <summary>
        /// 点到三角形轮廓的**有符号**距离：内部为负、外部为正。
        ///
        /// 有符号是必须的 —— 圆角那一步要往内缩，只用绝对值的话内缩方向就没了。
        /// 判断内外用射线法（向 +x 打一条线，数穿过轮廓边的次数，奇数次在内部）。
        /// </summary>
        private static float SignedDistanceToOutline(float px, float py, Vector2[] points)
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

                if ((a.y > py) != (b.y > py))
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

        /// <summary>三角形的内切半径（面积 / 半周长）。</summary>
        private static float InradiusOf(Vector2[] points)
        {
            Vector2 a = points[0];
            Vector2 b = points[1];
            Vector2 c = points[2];
            float perimeter = Vector2.Distance(b, c)
                + Vector2.Distance(a, c)
                + Vector2.Distance(a, b);
            if (perimeter < 0.0001f)
            {
                return 0f;
            }
            float area = Mathf.Abs(
                (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
            return area / (perimeter * 0.5f);
        }

        /// <summary>
        /// 把一个三角形按 <paramref name="inset"/> 的距离**向内收缩**。
        ///
        /// 做法是绕**内心**按 (r - inset) / r 缩放：三角形绕内心缩放时，
        /// 三条边到内心的距离同比变化，所以缩放后每条边都正好内移了
        /// <paramref name="inset"/>。不需要逐边算法线求交点。
        ///
        /// <paramref name="inradius"/> 由调用方算好传进来 —— 它已经和
        /// 外扩量一起夹过，这里不再夹，两边保持同一个数。
        /// </summary>
        private static Vector2[] InsetTriangle(Vector2[] points, float inset, float inradius)
        {
            Vector2 a = points[0];
            Vector2 b = points[1];
            Vector2 c = points[2];

            float perimeter = Vector2.Distance(b, c)
                + Vector2.Distance(a, c)
                + Vector2.Distance(a, b);
            if (perimeter < 0.0001f || inradius < 0.0001f)
            {
                return points;
            }

            // 内心 = 按对边长加权的顶点平均。
            Vector2 incenter = (a * Vector2.Distance(b, c)
                + b * Vector2.Distance(a, c)
                + c * Vector2.Distance(a, b)) / perimeter;

            float scale = (inradius - inset) / inradius;

            Vector2[] result = new Vector2[3];
            for (int i = 0; i < 3; i++)
            {
                result[i] = incenter + (points[i] - incenter) * scale;
            }
            return result;
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
