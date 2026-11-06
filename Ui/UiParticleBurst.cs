using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio.Ui
{
    /// <summary>
    /// 删除事件块时的爆炸粒子。
    ///
    /// **纯代码绘制，不用贴图、不用 ParticleSystem。** 粒子就是一批小方块，
    /// 每个有自己的初速度、重力、生命周期和透明度 —— 一个 `MaskableGraphic`
    /// 就能全部画进同一张网格，不额外占 draw call。
    ///
    /// 为什么不用 Unity 的 `ParticleSystem`：那是个完整子系统，要建材质、
    /// 要配渲染器，为了十来个方块启动一整套太重。而且它的粒子和 uGUI 的
    /// 层级/裁剪体系不共用，删块时容易画到面板外面去。
    ///
    /// **粒子颜色取自被删事件块的颜色** —— 用户明确要的。所以这里不是
    /// 固定配色，而是每个粒子按自己的来源块着色。
    /// </summary>
    internal sealed class UiParticleBurst : MaskableGraphic
    {
        private sealed class Particle
        {
            internal Vector2 Position;
            internal Vector2 Velocity;
            internal float Life;
            internal float MaxLife;
            internal float Size;
            internal Color Color;
            internal float Rotation;
            internal float AngularVelocity;
        }

        private readonly List<Particle> particles = new List<Particle>();

        /// <summary>重力（像素/秒²）。正值向下 —— 粒子的 y 轴和 uGUI 一致。</summary>
        private const float Gravity = 1400f;

        /// <summary>空气阻力（每秒衰减比例）。让粒子飞出去之后慢下来。</summary>
        private const float Drag = 2.2f;

        /// <summary>
        /// 从一个矩形区域炸出一簇粒子。
        ///
        /// `rect` 是**相对本组件**的坐标。调用方负责把块的位置换算过来。
        /// `color` 是块的底色 —— 粒子会在这个色相上做一点明暗抖动，
        /// 免得所有粒子一个色看着像一块实心。
        /// </summary>
        internal void Burst(Rect rect, Color color, int count)
        {
            int total = Mathf.Clamp(count, 4, 24);
            Vector2 center = rect.center;

            for (int i = 0; i < total; i++)
            {
                // 从块内部随机一个点出发，不是全部挤在中心。
                float startX = Random.Range(rect.xMin, rect.xMax);
                float startY = Random.Range(rect.yMin, rect.yMax);

                // 速度方向：从中心向外，加一点随机偏角。
                Vector2 outward = new Vector2(startX - center.x, startY - center.y);
                if (outward.sqrMagnitude < 0.01f)
                {
                    // 正好在中心时给一个随机方向，否则这个粒子不动。
                    float angle = Random.Range(0f, Mathf.PI * 2f);
                    outward = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                }
                outward.Normalize();
                // 偏角 ±40°，让飞散看起来不规整。
                float spread = Random.Range(-0.7f, 0.7f);
                float cos = Mathf.Cos(spread);
                float sin = Mathf.Sin(spread);
                Vector2 dir = new Vector2(
                    outward.x * cos - outward.y * sin,
                    outward.x * sin + outward.y * cos);

                float speed = Random.Range(180f, 420f);
                float life = Random.Range(0.32f, 0.52f);

                particles.Add(new Particle
                {
                    Position = new Vector2(startX, startY),
                    Velocity = dir * speed,
                    Life = life,
                    MaxLife = life,
                    Size = Random.Range(2.5f, 5.5f),
                    // 色相不变，只抖动明度 —— 粒子整体读起来还是块的颜色。
                    Color = new Color(
                        Mathf.Clamp01(color.r * Random.Range(0.75f, 1.25f)),
                        Mathf.Clamp01(color.g * Random.Range(0.75f, 1.25f)),
                        Mathf.Clamp01(color.b * Random.Range(0.75f, 1.25f)),
                        color.a),
                    Rotation = Random.Range(0f, Mathf.PI * 2f),
                    AngularVelocity = Random.Range(-12f, 12f)
                });
            }
            SetVerticesDirty();
        }

        /// <summary>
        /// 推进一帧。返回 true 表示还有活着的粒子（调用方据此决定要不要继续更新）。
        /// </summary>
        internal bool Step(float deltaTime)
        {
            if (particles.Count == 0)
            {
                return false;
            }
            if (deltaTime <= 0f)
            {
                return true;
            }

            for (int i = particles.Count - 1; i >= 0; i--)
            {
                Particle p = particles[i];
                p.Life -= deltaTime;
                if (p.Life <= 0f)
                {
                    particles.RemoveAt(i);
                    continue;
                }
                // 重力 + 阻力。阻力按指数衰减，比线性更像真实空气。
                p.Velocity.y += Gravity * deltaTime;
                p.Velocity *= Mathf.Exp(-Drag * deltaTime);
                p.Position += p.Velocity * deltaTime;
                p.Rotation += p.AngularVelocity * deltaTime;
            }
            SetVerticesDirty();
            return particles.Count > 0;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for (int i = 0; i < particles.Count; i++)
            {
                Particle p = particles[i];
                // 生命最后三分之一才开始淡出，前面保持实色 —— 一开始就淡
                // 会显得没力气。
                float fadeStart = p.MaxLife * 0.66f;
                float alpha = p.Life >= fadeStart
                    ? 1f
                    : Mathf.Clamp01(p.Life / fadeStart);
                Color32 color = p.Color;
                color.a = (byte)(color.a * alpha);

                AddQuad(vh, p, color);
            }
        }

        private static void AddQuad(VertexHelper vh, Particle p, Color32 color)
        {
            float half = p.Size * 0.5f;
            float cos = Mathf.Cos(p.Rotation);
            float sin = Mathf.Sin(p.Rotation);

            // 四个角按旋转角算出来，粒子会翻滚。
            Vector2 a = Rotate(new Vector2(-half, -half), cos, sin);
            Vector2 b = Rotate(new Vector2(half, -half), cos, sin);
            Vector2 c = Rotate(new Vector2(half, half), cos, sin);
            Vector2 d = Rotate(new Vector2(-half, half), cos, sin);

            Vector2 origin = p.Position;
            int start = vh.currentVertCount;
            // 局部 y 向下为正 → 顶点 y 取负（和项目里其它自绘组件同一换算）。
            vh.AddVert(new Vector3(origin.x + a.x, -(origin.y + a.y), 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(origin.x + b.x, -(origin.y + b.y), 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(origin.x + c.x, -(origin.y + c.y), 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(origin.x + d.x, -(origin.y + d.y), 0f), color, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static Vector2 Rotate(Vector2 v, float cos, float sin)
        {
            return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
        }
    }
}
