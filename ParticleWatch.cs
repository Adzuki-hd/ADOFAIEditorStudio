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
        /// 拖动播放头之后，把**拖动前本来就在播**的粒子放起来。
        ///
        /// 动机是「一拖播放头粒子就停」：定位会让装饰物按新时间重算，
        /// 粒子系统因此被清空，松手后没人负责重启。
        ///
        /// **但"恢复"只能恢复本来就该播的那些。** 以前这里对全场景每一件
        /// `scrParticleDecoration` 无条件 `Play()`，只要它没在播 ——
        /// 而绝大多数发射器在原版编辑器里本来就不该播（谱面没给它
        /// `SetParticle Start`，`autoPlay` 又被 `paused` 挡掉）。
        ///
        /// 实测（`ViLLAGE OF CHRYSANTHEMUM`）：一次拖动就在同一时刻点着了
        /// 全部 55 件，用户看到的就是「315 格附近一堆不该出现的粒子」——
        /// 三角形 / 花瓣 / 字符图那几种，本该 1800 格之后才登场。
        ///
        /// 所以判据是**快照**：拖动开始时记下谁在播，恢复时只播这些。
        /// 没在播的一律不碰 —— 它们该起来了会由
        /// `ParticlePreviewDomain` 里的 `SetParticle Start` 负责。
        /// </summary>
        private static scrParticleDecoration[] cache;
        private static int cacheFrame = -999;

        /// <summary>拖动开始时哪些实例在播。`null` 表示还没拍过快照。</summary>
        private static System.Collections.Generic.HashSet<int> playingWhenScrubBegan;

        /// <summary>
        /// 拖动开始：拍一张「现在谁在播」的快照。
        ///
        /// 必须**在拖动把系统清空之前**调用，否则快照永远是空的、
        /// 恢复也就什么都不会做（那正是拖动后粒子全停的老毛病）。
        /// </summary>
        internal static void BeginScrub()
        {
            scrParticleDecoration[] decorations = Collect();
            if (playingWhenScrubBegan == null)
            {
                playingWhenScrubBegan = new System.Collections.Generic.HashSet<int>();
            }
            playingWhenScrubBegan.Clear();
            for (int i = 0; i < decorations.Length; i++)
            {
                scrParticleDecoration decoration = decorations[i];
                if (decoration != null && decoration.particleSystem != null
                    && decoration.particleSystem.isPlaying)
                {
                    playingWhenScrubBegan.Add(decoration.GetInstanceID());
                }
            }
        }

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
            // **只恢复拖动前在播的那些。**
            //
            // 没拍过快照（没走 BeginScrub）时**什么都不做**：那说明这次
            // 调用不是"拖动后的恢复"，而无差别 Play() 正是这个 bug 的来源。
            if (playingWhenScrubBegan == null || playingWhenScrubBegan.Count == 0)
            {
                return;
            }

            scrParticleDecoration[] decorations = Collect();
            for (int i = 0; i < decorations.Length; i++)
            {
                scrParticleDecoration decoration = decorations[i];
                if (decoration == null || decoration.particleSystem == null)
                {
                    continue;
                }
                if (!playingWhenScrubBegan.Contains(decoration.GetInstanceID()))
                {
                    // 拖动前就没在播 —— 它本来就不该播，别去点它。
                    continue;
                }
                if (!decoration.particleSystem.isPlaying)
                {
                    decoration.particleSystem.Play();
                }
            }
        }

        /// <summary>
        /// 全场景的粒子装饰物，带缓存。
        ///
        /// 拖动时每次定位都会调到这里，不能每帧 `FindObjectsOfType` ——
        /// 缓存 60 帧。**但缓存里有已销毁对象时必须立刻重扫**：
        /// 定位会重建装饰物，攥着旧数组等于什么都没放起来
        /// （用户看到的就是「又卡住了」）。
        /// </summary>
        private static scrParticleDecoration[] Collect()
        {
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
            return cache;
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
