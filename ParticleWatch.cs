using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 临时监视：原生预览对象（`previewDec`）的粒子什么时候停。
    ///
    /// 用来确认「拖时间轴 → 粒子停」是不是**游戏侧**在重建装饰物 ——
    /// 我们的刷新路径已经用调用栈排除了，剩下的只能看这个系统本身的状态。
    /// 定性后整块删掉。
    /// </summary>
    internal static class ParticleWatch
    {

        private static readonly System.Collections.Generic.Dictionary<int, bool> playingState
            = new System.Collections.Generic.Dictionary<int, bool>();

        /// <summary>
        /// 拖动播放头之后把粒子重新放起来。
        ///
        /// 拖动时每次定位都会让装饰物按新时间重算，粒子系统因此被清空；
        /// 松手后没人负责重启，看起来就是「一拖播放头粒子就停」。
        /// </summary>
        private static scrParticleDecoration[] cache;
        private static int cacheFrame = -999;

        /// <summary>
        /// 时间轴正在拖播放头（或滚轮扫时间）。
        ///
        /// **粒子装饰物在这个时候别写位置。** 原生 `SetPosition` 会 `Clear()`
        /// 粒子系统，拖动时每帧定位一次 = 每帧清一次 —— 粒子永远是刚发射的
        /// 那一瞬间，看起来就是"被冻住"。拖动结束后写一次即可。
        /// </summary>
        internal static bool ScrubbingActive;

        internal static void ResumeAll()
        {
            // 拖动时每次定位都会调到这里，不能每帧 FindObjectsOfType —— 缓存 60 帧。
            // **但缓存里有已销毁对象时必须立刻重扫**：定位会重建装饰物，
            // 攥着旧数组等于什么都没放起来（用户看到的就是「又卡住了」）。
            bool stale = cache == null || cache.Length == 0;
            if (!stale)
            {
                for (int i = 0; i < cache.Length; i++)
                {
                    if (cache[i] == null)
                    {
                        stale = true;
                        break;
                    }
                }
            }
            if (stale || Time.frameCount - cacheFrame > 60)
            {
                cache = Object.FindObjectsByType<scrParticleDecoration>(FindObjectsSortMode.None);
                cacheFrame = Time.frameCount;
            }
            scrParticleDecoration[] decorations = cache;
            for (int i = 0; i < decorations.Length; i++)
            {
                scrParticleDecoration decoration = decorations[i];
                if (decoration == null || decoration.particleSystem == null)
                {
                    continue;
                }
                if (!decoration.particleSystem.isPlaying)
                {
                    decoration.particleSystem.Play();
                }
            }
        }

        internal static void Tick()
        {
            // 全场景扫描很贵，每 15 帧一次足够定位「拖动时停」。
            if (Time.frameCount % 15 != 0)
            {
                return;
            }

            scrParticleDecoration[] decorations =
                Object.FindObjectsByType<scrParticleDecoration>(FindObjectsSortMode.None);
            for (int i = 0; i < decorations.Length; i++)
            {
                scrParticleDecoration decoration = decorations[i];
                if (decoration == null || decoration.particleSystem == null)
                {
                    continue;
                }
                int id = decoration.GetInstanceID();
                bool playing = decoration.particleSystem.isPlaying;
                bool previous;
                if (playingState.TryGetValue(id, out previous) && previous && !playing)
                {
                    Main.Logger.Log("[粒子监视] " + decoration.name + " 停了 帧="
                        + Time.frameCount + " 粒子数="
                        + decoration.particleSystem.particleCount);
                }
                playingState[id] = playing;
            }
        }
    }
}
