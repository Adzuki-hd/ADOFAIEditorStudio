using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 粒子参数的数据层：把 `ParticleSystem` 各模块的读写收在一处。
    ///
    /// **模块是 struct**，`var m = system.main; m.startSize = …; system.main = m;`
    /// 这种"取-改-赋回"少一步就什么都不会变（值是拷贝），所以全部集中在这里，
    /// 面板那边只调属性。
    ///
    /// 角度对外用**度**（和原版面板一致），内部换弧度；时间/大小沿用系统单位。
    /// </summary>
    internal sealed class ParticleBinding
    {
        private readonly ParticleSystem system;
        private readonly scrParticleDecoration decoration;

        internal ParticleBinding(scrParticleDecoration target)
        {
            decoration = target;
            system = target == null ? null : target.particleSystem;
        }

        internal bool Valid { get { return system != null; } }

        internal ParticleSystem System { get { return system; } }

        // ── 形状页 ─────────────────────────────────────────────────

        private Material cachedMaterial;

        /// <summary>
        /// 随机纹理平铺：改的是渲染器材质的 `mainTextureScale`。
        ///
        /// 用实例化材质（`material` 而不是 `sharedMaterial`）—— 改共享材质会
        /// 把所有用同一张图的粒子一起改掉，那是资源污染，不是参数编辑。
        /// </summary>
        internal Vector2 TextureTiling
        {
            get
            {
                Material material = GetMaterial();
                return material == null ? Vector2.one : material.mainTextureScale;
            }
            set
            {
                Material material = GetMaterial();
                if (material == null)
                {
                    return;
                }
                material.mainTextureScale = new Vector2(
                    Mathf.Max(0.01f, value.x), Mathf.Max(0.01f, value.y));
            }
        }

        /// <summary>当前贴图名（只用于显示）。</summary>
        internal string TextureName
        {
            get
            {
                Material material = GetMaterial();
                return material == null || material.mainTexture == null
                    ? string.Empty : material.mainTexture.name;
            }
        }

        /// <summary>
        /// 选一张图并换到粒子上。
        ///
        /// 走原生选择器（它会把文件复制进谱面目录并返回文件名），再通过
        /// `TextureManager` 拿到 sprite —— 和参数栏里图片参数的取图路径一致。
        /// </summary>
        internal string PickImage()
        {
            string picked = RDEditorUtils.ShowFileSelectorForImage(
                RDString.Get("editor.dialog.selectImage"), -1L);
            if (string.IsNullOrEmpty(picked))
            {
                return null;
            }

            Material material = GetMaterial();
            if (material == null)
            {
                return picked;
            }

            scrDecorationManager manager = scrDecorationManager.instance;
            TextureManager holder = manager == null ? null : manager.imageHolder;
            Sprite sprite = null;
            if (holder != null && holder.customSprites != null)
            {
                TextureManager.CustomSprite custom;
                if (holder.customSprites.TryGetValue(picked, out custom)
                    && custom != null)
                {
                    sprite = custom.sprite;
                }
            }
            if (sprite != null && sprite.texture != null)
            {
                material.mainTexture = sprite.texture;
            }
            return picked;
        }

        private Material GetMaterial()
        {
            if (cachedMaterial != null)
            {
                return cachedMaterial;
            }
            if (!Valid)
            {
                return null;
            }
            ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
            {
                return null;
            }
            cachedMaterial = renderer.material;
            return cachedMaterial;
        }

        internal float StartLifetime
        {
            get { return Valid ? system.main.startLifetime.constant : 0f; }
            set
            {
                if (!Valid) return;
                ParticleSystem.MainModule main = system.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(Mathf.Max(0.01f, value));
            }
        }

        internal float StartSize
        {
            get { return Valid ? system.main.startSize.constant : 0f; }
            set
            {
                if (!Valid) return;
                ParticleSystem.MainModule main = system.main;
                main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(0f, value));
            }
        }

        /// <summary>开始旋转，单位度。</summary>
        internal float StartRotationDegrees
        {
            get { return Valid ? system.main.startRotation.constant * Mathf.Rad2Deg : 0f; }
            set
            {
                if (!Valid) return;
                ParticleSystem.MainModule main = system.main;
                main.startRotation = new ParticleSystem.MinMaxCurve(value * Mathf.Deg2Rad);
            }
        }

        internal float StartSpeed
        {
            get { return Valid ? system.main.startSpeed.constant : 0f; }
            set
            {
                if (!Valid) return;
                ParticleSystem.MainModule main = system.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(value);
            }
        }

        internal Color StartColor
        {
            get { return Valid ? system.main.startColor.color : Color.white; }
            set
            {
                if (!Valid) return;
                ParticleSystem.MainModule main = system.main;
                main.startColor = new ParticleSystem.MinMaxGradient(value);
            }
        }

        internal float EmissionRate
        {
            get { return Valid ? system.emission.rateOverTime.constant : 0f; }
            set
            {
                if (!Valid) return;
                ParticleSystem.EmissionModule emission = system.emission;
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(Mathf.Max(0f, value));
            }
        }

        internal int BurstCount
        {
            get
            {
                if (!Valid) return 0;
                ParticleSystem.Burst[] bursts = new ParticleSystem.Burst[system.emission.burstCount];
                system.emission.GetBursts(bursts);
                return bursts.Length == 0 ? 0 : Mathf.RoundToInt(bursts[0].count.constant);
            }
            set
            {
                if (!Valid) return;
                ParticleSystem.EmissionModule emission = system.emission;
                if (value <= 0)
                {
                    emission.SetBursts(new ParticleSystem.Burst[0]);
                }
                else
                {
                    emission.SetBursts(new[]
                    {
                        new ParticleSystem.Burst(0f, (short)Mathf.Max(1, value))
                    });
                }
            }
        }

        internal bool EmissionEnabled
        {
            get { return Valid && system.emission.enabled; }
            set
            {
                if (!Valid) return;
                ParticleSystem.EmissionModule emission = system.emission;
                emission.enabled = value;
            }
        }

        // ── 变换页 ─────────────────────────────────────────────────

        /// <summary>颜色随时间（渐变）。</summary>
        internal Gradient ColorOverLifetime
        {
            get
            {
                return Valid ? system.colorOverLifetime.color.gradient : null;
            }
            set
            {
                if (!Valid || value == null) return;
                ParticleSystem.ColorOverLifetimeModule module = system.colorOverLifetime;
                module.enabled = true;
                module.color = new ParticleSystem.MinMaxGradient(value);
            }
        }

        internal bool ColorOverLifetimeEnabled
        {
            get { return Valid && system.colorOverLifetime.enabled; }
            set
            {
                if (!Valid) return;
                ParticleSystem.ColorOverLifetimeModule module = system.colorOverLifetime;
                module.enabled = value;
            }
        }

        internal bool SizeOverLifetimeEnabled
        {
            get { return Valid && system.sizeOverLifetime.enabled; }
            set
            {
                if (!Valid) return;
                ParticleSystem.SizeOverLifetimeModule module = system.sizeOverLifetime;
                module.enabled = value;
            }
        }

        /// <summary>形状弧（度）。</summary>
        internal float ArcDegrees
        {
            get { return Valid ? system.shape.arc * Mathf.Rad2Deg : 0f; }
            set
            {
                if (!Valid) return;
                ParticleSystem.ShapeModule shape = system.shape;
                shape.arc = Mathf.Clamp(value, 0f, 360f) * Mathf.Deg2Rad;
            }
        }

        internal ParticleSystemShapeType ShapeType
        {
            get { return Valid ? system.shape.shapeType : ParticleSystemShapeType.Cone; }
            set
            {
                if (!Valid) return;
                ParticleSystem.ShapeModule shape = system.shape;
                shape.shapeType = value;
            }
        }

        /// <summary>改完模块后统一收口：让系统按新参数重新起。</summary>
        internal void Restart()
        {
            if (!Valid) return;
            system.Clear();
            system.Play();
        }
    }
}
