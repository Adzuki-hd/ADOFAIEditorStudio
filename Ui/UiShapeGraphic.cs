using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio.Ui
{
    /// <summary>
    /// 矢量圆角矩形 —— **按当前尺寸现算几何，不采样贴图**。
    ///
    /// 为什么要它：原来所有圆角都是拿一张 32×32 的贴图做九宫格拉伸，
    /// 半径写死 7 像素。面板一大，7px 的圆角就显得钝、像老式控件；
    /// 而贴图拉伸在非整数像素位置还会糊边。这些正是「像素感」的来源。
    ///
    /// 这里改成按实际宽高生成圆角多边形（每个圆角用若干段直线近似圆弧），
    /// 半径可以随控件尺寸走，边缘由几何决定而不是采样决定。
    ///
    /// **性能**：一个组件把**多个形状**画进同一张网格，和
    /// `ModulePolygonGraphic` 同一个思路 —— 不是每个圆角矩形一个 draw call。
    /// 段数按半径自适应（半径小就少画几段），避免小控件浪费顶点。
    /// 内容没变时不重建网格。
    /// </summary>
    internal sealed class UiShapeGraphic : MaskableGraphic
    {
        /// <summary>
        /// 一个要画的圆角矩形。坐标相对本组件原点。
        ///
        /// **`Fill` 是"额外的染色"**：最终颜色 = `Fill × Graphic.color`，
        /// 和 `Image` 的语义一致（`Image` 也是把 `color` 乘到 sprite 上）。
        /// 这样把它当 `Button.targetGraphic` 用时，按钮的着色/高亮照样生效。
        /// </summary>
        internal sealed class RoundedRect
        {
            internal Rect Bounds;
            internal float Radius;
            internal Color Fill = Color.white;
            internal Color Stroke = Color.white;
            internal float StrokeWidth;
        }

        private readonly List<RoundedRect> pending = new List<RoundedRect>();
        private readonly List<RoundedRect> submitted = new List<RoundedRect>();
        private bool forceDirty = true;

        /// <summary>圆角近似用的段数上限。半径再大，8 段也看不出棱角了。</summary>
        private const int MaxCornerSegments = 8;

        internal void BeginShapes()
        {
            pending.Clear();
        }

        internal void AddRoundedRect(Rect bounds, float radius, Color fill,
            Color stroke, float strokeWidth)
        {
            if (bounds.width <= 0f || bounds.height <= 0f)
            {
                return;
            }
            pending.Add(new RoundedRect
            {
                Bounds = bounds,
                Radius = Mathf.Max(0f, radius),
                Fill = fill,
                Stroke = stroke,
                StrokeWidth = Mathf.Max(0f, strokeWidth)
            });
        }

        /// <summary>
        /// 提交这一帧的形状。**内容没变就不重建网格** ——
        /// 和 `ModulePolygonGraphic` 同一套脏判断，避免每帧重算顶点。
        /// </summary>
        internal void SubmitShapes()
        {
            bool changed = forceDirty || pending.Count != submitted.Count;
            if (!changed)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    RoundedRect a = pending[i];
                    RoundedRect b = submitted[i];
                    if (a.Bounds != b.Bounds
                        || !Mathf.Approximately(a.Radius, b.Radius)
                        || a.Fill != b.Fill
                        || a.Stroke != b.Stroke
                        || !Mathf.Approximately(a.StrokeWidth, b.StrokeWidth))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed)
            {
                return;
            }

            submitted.Clear();
            for (int i = 0; i < pending.Count; i++)
            {
                RoundedRect source = pending[i];
                submitted.Add(new RoundedRect
                {
                    Bounds = source.Bounds,
                    Radius = source.Radius,
                    Fill = source.Fill,
                    Stroke = source.Stroke,
                    StrokeWidth = source.StrokeWidth
                });
            }
            forceDirty = false;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for (int i = 0; i < submitted.Count; i++)
            {
                DrawRoundedRect(vh, submitted[i]);
            }
        }

        private void DrawRoundedRect(VertexHelper vh, RoundedRect shape)
        {
            Rect bounds = shape.Bounds;
            // 半径不能超过短边的一半，否则四个圆角会互相吃进去。
            float radius = Mathf.Min(shape.Radius,
                Mathf.Min(bounds.width, bounds.height) * 0.5f);

            Color32 fill = shape.Fill * color;

            // ── 填充：**内圈实心 + 外圈羽化带**，用来做抗锯齿 ──
            //
            // 纯几何的圆角是硬边，斜着那一小段弧在屏幕上就是锯齿
            // （用户看到的「颗粒感」）。原来那张贴图靠距离场算 1px 软边
            // 解决了这个问题，矢量版必须自己补上：
            //
            //   内圈：半径内缩半个羽化宽，实心不透明
            //   外圈：内圈到原轮廓之间的环，顶点 alpha 渐变到 0
            //
            // 两圈用同一个段数，可以一一对应拼四边形。
            if (fill.a > 0)
            {
                const float feather = 1f;
                float innerRadius = Mathf.Max(0f, radius - feather * 0.5f);
                List<Vector2> outerRing = BuildRoundedOutline(bounds, radius);
                List<Vector2> innerRing = BuildRoundedOutline(
                    InsetRect(bounds, feather * 0.5f), innerRadius);

                if (outerRing.Count >= 3 && innerRing.Count >= 3)
                {
                    Vector2 center = bounds.center;
                    Color32 transparent = new Color32(fill.r, fill.g, fill.b, 0);

                    // 内圈三角扇（实心）。
                    int innerCenter = vh.currentVertCount;
                    vh.AddVert(new Vector3(center.x, -center.y, 0f), fill, Vector2.zero);
                    for (int i = 0; i < innerRing.Count; i++)
                    {
                        Vector2 p = innerRing[i];
                        vh.AddVert(new Vector3(p.x, -p.y, 0f), fill, Vector2.zero);
                    }
                    for (int i = 0; i < innerRing.Count; i++)
                    {
                        int a = innerCenter + 1 + i;
                        int b = innerCenter + 1 + (i + 1) % innerRing.Count;
                        vh.AddTriangle(innerCenter, a, b);
                    }

                    // 外圈羽化带：外圈顶点透明、内圈顶点实心。
                    int ringStart = vh.currentVertCount;
                    for (int i = 0; i < outerRing.Count; i++)
                    {
                        Vector2 p = outerRing[i];
                        vh.AddVert(new Vector3(p.x, -p.y, 0f), transparent, Vector2.zero);
                    }
                    for (int i = 0; i < innerRing.Count; i++)
                    {
                        Vector2 p = innerRing[i];
                        vh.AddVert(new Vector3(p.x, -p.y, 0f), fill, Vector2.zero);
                    }
                    int outerBase = ringStart;
                    int innerBase = ringStart + outerRing.Count;
                    for (int i = 0; i < innerRing.Count; i++)
                    {
                        int iNext = (i + 1) % innerRing.Count;
                        vh.AddTriangle(outerBase + i, outerBase + iNext, innerBase + iNext);
                        vh.AddTriangle(outerBase + i, innerBase + iNext, innerBase + i);
                    }
                }
            }

            // ── 描边：每段两条线拼一个矩形 ──
            Color32 stroke = shape.Stroke * color;
            if (shape.StrokeWidth > 0f && stroke.a > 0)
            {
                List<Vector2> outline = BuildRoundedOutline(bounds, radius);
                float half = shape.StrokeWidth * 0.5f;
                for (int i = 0; i < outline.Count; i++)
                {
                    Vector2 from = outline[i];
                    Vector2 to = outline[(i + 1) % outline.Count];
                    Vector2 delta = to - from;
                    float length = delta.magnitude;
                    if (length < 0.001f)
                    {
                        continue;
                    }
                    Vector2 normal = new Vector2(-delta.y, delta.x) / length * half;
                    AddQuad(vh, from - normal, to - normal, to + normal, from + normal,
                        stroke);
                }
            }
        }

        /// <summary>
        /// 生成圆角矩形的外轮廓。四个角各用若干段直线近似圆弧。
        ///
        /// **段数按半径自适应**：半径 3px 只画 3 段就够（小于 1px 的弧线差别
        /// 看不出来），半径 8px 以上画满 8 段。这样小控件不浪费顶点，
        /// 大控件也不会显出棱角。
        /// </summary>
        private static List<Vector2> BuildRoundedOutline(Rect bounds, float radius)
        {
            List<Vector2> points = new List<Vector2>(40);
            if (radius <= 0.01f)
            {
                points.Add(new Vector2(bounds.xMin, bounds.yMin));
                points.Add(new Vector2(bounds.xMax, bounds.yMin));
                points.Add(new Vector2(bounds.xMax, bounds.yMax));
                points.Add(new Vector2(bounds.xMin, bounds.yMax));
                return points;
            }

            int segments = Mathf.Clamp(Mathf.CeilToInt(radius), 3, MaxCornerSegments);

            // 四个角的圆心，顺时针：左下 → 右下 → 右上 → 左上。
            Vector2[] centers =
            {
                new Vector2(bounds.xMin + radius, bounds.yMin + radius),
                new Vector2(bounds.xMax - radius, bounds.yMin + radius),
                new Vector2(bounds.xMax - radius, bounds.yMax - radius),
                new Vector2(bounds.xMin + radius, bounds.yMax - radius)
            };
            // 每个角从哪个角度开始扫（弧度）：左下 180°、右下 270°、右上 0°、左上 90°。
            float[] startAngles = { Mathf.PI, Mathf.PI * 1.5f, 0f, Mathf.PI * 0.5f };

            for (int corner = 0; corner < 4; corner++)
            {
                Vector2 center = centers[corner];
                float startAngle = startAngles[corner];
                for (int i = 0; i <= segments; i++)
                {
                    float angle = startAngle + (Mathf.PI * 0.5f) * i / segments;
                    points.Add(new Vector2(
                        center.x + Mathf.Cos(angle) * radius,
                        center.y + Mathf.Sin(angle) * radius));
                }
            }
            return points;
        }

        /// <summary>
        /// 把矩形四边各向内收 `inset` 像素。用于生成羽化带的内圈。
        ///
        /// 收到比零还小时返回一个零尺寸矩形（调用方会跳过绘制），
        /// 免得出现负宽高把三角剖分弄反。
        /// </summary>
        private static Rect InsetRect(Rect rect, float inset)
        {
            float x = rect.x + inset;
            float y = rect.y + inset;
            float w = rect.width - inset * 2f;
            float h = rect.height - inset * 2f;
            if (w <= 0f || h <= 0f)
            {
                return new Rect(rect.center.x, rect.center.y, 0f, 0f);
            }
            return new Rect(x, y, w, h);
        }

        private static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c,
            Vector2 d, Color32 color)
        {
            int start = vh.currentVertCount;
            vh.AddVert(new Vector3(a.x, -a.y, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(b.x, -b.y, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(c.x, -c.y, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(d.x, -d.y, 0f), color, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
