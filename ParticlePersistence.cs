using System;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 把粒子面板改过的模块值**写回事件数据**（落盘）。
    ///
    /// 键名来自事件属性表的实测清单（`AddParticle` 共 37 项），对应关系：
    ///
    ///   maxParticles        ↔ main.maxParticles
    ///   simulationSpeed     ↔ main.simulationSpeed（事件里是百分比）
    ///   playDuration        ↔ main.duration
    ///   loop                ↔ main.loop
    ///   particleLifetime    ↔ main.startLifetime（FloatPair）
    ///   particleSize        ↔ main.startSize（FloatPair，事件里是百分比）
    ///   startRotation       ↔ main.startRotation（FloatPair，度）
    ///   color               ↔ main.startColor（MinMaxGradient）
    ///   emissionRate        ↔ emission.rateOverTime（FloatPair）
    ///   arc                 ↔ shape.arc（度）
    ///   randomTextureTiling ↔ 渲染器材质 mainTextureScale
    ///
    /// 写回走 `EditorPropertyPanel.Commit`，所以只读拦截、撤销历史都沿用同一条路。
    /// </summary>
    internal static class ParticlePersistence
    {
        internal static void Apply(EditorStudioOverlay overlay, LevelEvent target,
            ParticleSystem system)
        {
            if (overlay == null || target == null || system == null
                || target.info == null || target.info.propertiesInfo == null)
            {
                return;
            }
            if (!overlay.AllowEdit())
            {
                return;
            }

            ParticleSystem.MainModule main = system.main;
            ParticleSystem.EmissionModule emission = system.emission;
            ParticleSystem.ShapeModule shape = system.shape;

            Commit(overlay, target, "maxParticles", main.maxParticles);
            Commit(overlay, target, "simulationSpeed", main.simulationSpeed * 100f);
            Commit(overlay, target, "playDuration", main.duration);
            Commit(overlay, target, "loop", main.loop);

            Commit(overlay, target, "particleLifetime", Pair(main.startLifetime));
            Commit(overlay, target, "particleSize",
                Scale(Pair(main.startSize), 100f));
            Commit(overlay, target, "startRotation",
                Scale(Pair(main.startRotation), Mathf.Rad2Deg));
            Commit(overlay, target, "emissionRate", Pair(emission.rateOverTime));
            Commit(overlay, target, "arc", shape.arc * Mathf.Rad2Deg);

            Color color = main.startColor.color;
            Commit(overlay, target, "color",
                new ADOFAI.Editor.Models.SerializedMinMaxGradient
                {
                    mode = ParticleSystemGradientMode.TwoColors,
                    color1 = EditorPropertyPanel.ColorToHex(color),
                    color2 = EditorPropertyPanel.ColorToHex(color)
                });

            ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer != null && renderer.material != null)
            {
                Commit(overlay, target, "randomTextureTiling",
                    renderer.material.mainTextureScale);
            }
        }

        private static void Commit(EditorStudioOverlay overlay, LevelEvent target,
            string key, object value)
        {
            if (value == null || !target.info.propertiesInfo.ContainsKey(key))
            {
                return;
            }
            try
            {
                overlay.PropertyPanel.Commit(target, key, value);
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[粒子落盘] " + key + " 写入失败：" + exception.Message);
            }
        }

        private static Tuple<float, float> Pair(ParticleSystem.MinMaxCurve curve)
        {
            return new Tuple<float, float>(curve.constantMin, curve.constantMax);
        }

        private static Tuple<float, float> Scale(Tuple<float, float> value, float factor)
        {
            return new Tuple<float, float>(value.Item1 * factor, value.Item2 * factor);
        }
    }
}
