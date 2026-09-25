using System.Collections.Generic;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// 把「球已经踩过的格」点亮。
    ///
    /// 为什么需要：轨道的辉度（glow）**不是由 glowMultiplier 决定显示与否的**。
    /// scrFloor.Start() 里算过一次：
    ///
    ///     active = tileFlashStyle == AlwaysOn
    ///           || (hasLit && tileFlashStyle != AlwaysBlack);
    ///     topGlow.gameObject.SetActive(active);
    ///
    /// 而格子刚建出来时 hasLit 是 false，所以辉度精灵默认是关着的；
    /// 之后只有 LightUp() 会把 hasLit 置位并重新打开它——那是
    /// 球落到这一格上时调的。
    ///
    /// 求值器驱动了球的位置，却没走过这条路，于是预览里没有任何格
    /// 是「已点亮」的，glowMultiplier 写得再对也没东西去渲染——
    /// 表现就是「重新设置轨道颜色里的辉度完全没生效」。
    ///
    /// **只做到「点亮」为止，不调 SetToRandomColor()。** LightUp() 里
    /// 紧跟的那句会给格子随机上色，那是原版踩格子时的效果；
    /// 在这里跑会和 RecolorTrack 求出来的颜色打架。
    /// </summary>
    internal sealed class LightedTrackPreviewDomain : IPreviewDomain
    {
        public string Name
        {
            get { return "LightedTrack"; }
        }

        private PreviewTimelineContext context;
        private int floorCount;

        // 上一帧点亮到哪一格。只在它变化时处理两端之间的差，
        // 不每帧扫全部格——谱面动辄几千格。
        private int litThrough = -1;

        public void Analyze(PreviewTimelineContext source)
        {
            context = source;
            floorCount = source == null ? 0 : source.Floors.Count;
            litThrough = -1;
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            if (context == null || floorCount == 0)
            {
                return;
            }

            int target = Mathf.Clamp(context.FindFloorAtOrBefore(frame.Time), -1, floorCount - 1);
            if (target == litThrough)
            {
                return;
            }

            if (target > litThrough)
            {
                for (int i = Mathf.Max(0, litThrough + 1); i <= target; i++)
                {
                    SetLit(i, true);
                }
            }
            else
            {
                for (int i = litThrough; i > target; i--)
                {
                    SetLit(i, false);
                }
            }
            litThrough = target;
        }

        /// <summary>
        /// 复现 LightUp() 里点亮那一段（跳过着色）。反向着走时熄灭。
        /// </summary>
        private void SetLit(int floorIndex, bool lit)
        {
            scrFloor floor = floorIndex >= 0 && floorIndex < floorCount
                ? context.GetFloorBinding(floorIndex)
                : null;
            if (floor == null)
            {
                return;
            }

            floor.hasLit = lit;

            scrVfx vfx = scrVfx.instance;
            if (vfx == null)
            {
                return;
            }

            // 与原版 Start() 里那条判定保持一致：AlwaysBlack 是「永不发光」，
            // MoveToTopLayer 走的是另一条（改排序层，不动辉度精灵）。
            bool glowAllowed = vfx.tileFlashStyle != TileFlashStyle.AlwaysBlack
                && vfx.tileFlashStyle != TileFlashStyle.MoveToTopLayer
                && !floor.disableGlow;

            bool active = glowAllowed
                && (lit || vfx.tileFlashStyle == TileFlashStyle.AlwaysOn);

            if (floor.topGlow != null && floor.topGlow.gameObject.activeSelf != active)
            {
                floor.topGlow.gameObject.SetActive(active);
            }
            if (floor.bottomGlow != null && floor.bottomGlow.gameObject.activeSelf != active)
            {
                floor.bottomGlow.gameObject.SetActive(active);
            }
        }

        public void EndSession()
        {
            context = null;
            floorCount = 0;
            litThrough = -1;
        }
    }
}
