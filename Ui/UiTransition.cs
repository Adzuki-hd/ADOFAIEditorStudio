using System;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio.Ui
{
    /// <summary>
    /// 界面过渡：淡入淡出 + 轻微缩放。挂在一个节点上就能用。
    ///
    /// **不用 DOTween。** 项目里虽然引了 `DG.Tweening`（游戏自带），但那是
    /// 谱面播放用的；UI 这边只需要"透明度 + 缩放"两个属性随时间变，
    /// 自己算比配一套 tween 对象轻，也不会和游戏的 tween 池抢资源。
    ///
    /// 用法：
    ///   `PlayShow()` —— 从"透明 + 略小"过渡到"不透明 + 原始大小"
    ///   `PlayHide(Action onDone)` —— 反向，结束时回调（用来真正销毁/隐藏）
    ///
    /// **缩放是围绕中心缩的**，所以挂之前节点的 pivot 应该是 (0.5, 0.5)；
    /// 左对齐的控件如果 pivot 在左上角，缩放会从左上角长出来，看着像"甩"。
    /// 需要的话调 `SetScalePivot`。
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    internal sealed class UiTransition : MonoBehaviour
    {
        private CanvasGroup group;
        private RectTransform rect;

        private bool playing;
        private bool showing;
        private float elapsed;
        private float duration;
        private Action onComplete;

        /// <summary>起始缩放。0.96 是"几乎没有，但能感觉到"的那一档。</summary>
        private const float HiddenScale = 0.96f;

        /// <summary>
        /// 横向滑动的距离（像素）。**只在 `PlaySlide` 里用**。
        ///
        /// 原来取 32 太小 —— 在 1000px 宽的页面上几乎看不出位移。
        /// 80 是"明显在滑、但不至于从屏幕外飞进来"的量级。
        /// </summary>
        private const float SlideDistance = 80f;

        /// <summary>这一轮要不要带横向位移。`PlayShow` 时为 false。</summary>
        private bool sliding;

        /// <summary>正在播过渡吗。调用方据此避免重复触发同一段动画。</summary>
        internal bool IsPlaying { get { return playing; } }
        /// <summary>横向位移的方向（+1 向右，-1 向左）。</summary>
        private float slideDirection;

        // 播放前记下的基准：位置、尺寸、pivot。缩放时按这三样算补偿偏移。
        private Vector2 basePosition;
        private Vector2 baseSize;
        private Vector2 basePivot;
        private bool baseCaptured;

        private void Awake()
        {
            group = GetComponent<CanvasGroup>();
            rect = transform as RectTransform;
        }

        /// <summary>给节点挂上过渡组件（已经有了就复用）。</summary>
        internal static UiTransition Attach(GameObject target)
        {
            if (target == null)
            {
                return null;
            }
            UiTransition transition = target.GetComponent<UiTransition>();
            if (transition == null)
            {
                // CanvasGroup 必须先有 —— `RequireComponent` 会要求它。
                if (target.GetComponent<CanvasGroup>() == null)
                {
                    target.AddComponent<CanvasGroup>();
                }
                transition = target.AddComponent<UiTransition>();
            }
            return transition;
        }

        /// <summary>
        /// **不改 pivot，用位置补偿实现"从中心缩放"。**
        ///
        /// 原来这里直接把 pivot 改成 (0.5, 0.5) 再补 `anchoredPosition`，
        /// 但调用方的布局函数（`SetTopLeft`）每帧都把 pivot 强制回左上角 ——
        /// 两边打架，菜单背景就歪了。
        ///
        /// 现在完全不碰 pivot：记下**原始**的 `anchoredPosition` 和尺寸，
        /// 缩放时按 `(1 - scale) * size * pivot` 补偿偏移。pivot 在左上角时
        /// 这正好把"从左上角放大"变成"从中心放大"，而布局函数看到的
        /// pivot 和锚点始终没变。
        /// </summary>
        private void CaptureBase()
        {
            if (rect == null || baseCaptured)
            {
                return;
            }
            basePosition = rect.anchoredPosition;
            baseSize = rect.rect.size;
            basePivot = rect.pivot;
            baseCaptured = true;
        }

        /// <summary>丢掉记下的基准，下次播放时重新捕获（位置被布局改过之后调）。</summary>
        internal void ResetBase()
        {
            baseCaptured = false;
        }

        internal void PlayShow(float seconds)
        {
            if (rect == null)
            {
                Awake();
            }
            // **先还原到基准，再重新捕获。**
            //
            // 两次捕获之间节点可能被上一次的 `Apply` 推到了偏移位置 ——
            // 直接捕获会把偏移当成新基准，开关几次就越飘越远。
            // 先还原，捕获到的才是真实位置。
            //
            // 菜单每次弹出来位置不同，靠的是调用方在 `PlayShow` 之前
            // 已经 `SetTopLeft` 摆好了位置；还原只把缩放补偿撤掉，
            // 不动那个位置。
            RestoreBasePose();
            baseCaptured = false;
            CaptureBase();
            sliding = false;
            showing = true;
            playing = true;
            elapsed = 0f;
            duration = Mathf.Max(0.01f, seconds);
            onComplete = null;
            // 先摆到起始状态，第一帧才不会闪一下完整的。
            Apply(0f);
        }

        /// <summary>
        /// 横向滑动入场：从 `direction` 那一侧滑进来 + 淡入。
        ///
        /// `direction` 是"**从哪边来**"：+1 从右边来（往左滑入），
        /// -1 从左边来。往右切页时新页从右边来，读起来就是"内容往左翻"。
        /// </summary>
        internal void PlaySlide(float seconds, float direction)
        {
            if (rect == null)
            {
                Awake();
            }
            RestoreBasePose();
            baseCaptured = false;
            CaptureBase();
            sliding = true;
            slideDirection = direction >= 0f ? 1f : -1f;
            showing = true;
            playing = true;
            elapsed = 0f;
            duration = Mathf.Max(0.01f, seconds);
            onComplete = null;
            Apply(0f);
        }

        /// <summary>
        /// 横向滑动**出场**：往 `direction` 的反方向滑走 + 淡出。
        ///
        /// 和新页的 `PlaySlide` 配合，两边同时播就是交叉滑动 ——
        /// 旧页往左走、新页从右边来，中途两页都在，读起来是连贯的翻页。
        ///
        /// 播完回调里调方才该真正 `SetActive(false)`。
        /// </summary>
        internal void PlaySlideOut(float seconds, float direction, Action onDone)
        {
            if (rect == null)
            {
                Awake();
            }
            RestoreBasePose();
            baseCaptured = false;
            CaptureBase();
            sliding = true;
            // **方向取反**：新页从右边来（+1）时，旧页要往左边走。
            slideDirection = direction >= 0f ? -1f : 1f;
            showing = false;
            playing = true;
            elapsed = 0f;
            duration = Mathf.Max(0.01f, seconds);
            onComplete = onDone;
            Apply(1f);
        }

        internal void PlayHide(float seconds, Action onDone)
        {
            if (rect == null)
            {
                Awake();
            }
            // **不重新捕获基准。** 隐藏时节点正处在"完全显示"状态，
            // 位置已经被上一次 `Apply` 补偿过 —— 这时候捕获会把补偿后的
            // 位置当成基准，越播越偏。沿用显示时记下的那份才对。
            if (!baseCaptured)
            {
                CaptureBase();
            }
            showing = false;
            playing = true;
            elapsed = 0f;
            duration = Mathf.Max(0.01f, seconds);
            onComplete = onDone;
            // 从"完全显示"开始往隐藏走，不是从 0 开始（那会先跳成透明）。
            Apply(1f);
        }

        private void Update()
        {
            if (!playing)
            {
                return;
            }
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            // `progress` 一律是"显示程度"：1 = 完全显示，0 = 完全隐藏。
            //   · 显示：从 0 走到 1，用缓出（开头快结尾慢，有落定感）
            //   · 隐藏：从 1 走到 0，用缓入（开头慢结尾快，消失得干脆）
            float eased = showing
                ? 1f - (1f - t) * (1f - t) * (1f - t)
                : 1f - t * t * t;
            Apply(eased);

            if (t < 1f)
            {
                return;
            }
            playing = false;
            if (showing)
            {
                // 播完把状态归位，免得残留缩放影响后续布局。
                Apply(1f);
                return;
            }
            // **隐藏播完不要再 `Apply(1f)`。** 那时 `progress` 已经是 0
            // （完全透明），设回 1 会让窗口"闪一下又消失"——
            // 用户看到的就是关闭时闪几次。
            //
            // 但**位置要还原**：`Apply(0)` 已经把 `anchoredPosition` 推到了
            // 偏移最大的地方，不还原的话下次 `PlayShow` 捕获基准时拿到的
            // 就是这个偏移值，开关几次窗口会越飘越远（用户报的"往左下角偏"）。
            RestoreBasePose();
            Action callback = onComplete;
            onComplete = null;
            if (callback != null)
            {
                callback();
            }
        }

        /// <summary>
        /// 把节点摆回"完全显示、无缩放"的基准状态。
        ///
        /// **隐藏播完必须调。** 否则 `Apply(0)` 留下的偏移会被下一次
        /// `CaptureBase` 当成新基准，开关几次就越飘越远。
        /// </summary>
        private void RestoreBasePose()
        {
            if (rect == null)
            {
                return;
            }
            rect.localScale = Vector3.one;
            if (baseCaptured)
            {
                rect.anchoredPosition = basePosition;
            }
            // 滑动状态也要清 —— 否则下次 `PlayShow`（不带滑动）会残留位移。
            sliding = false;
        }

        /// <summary>`progress` 0 = 完全隐藏，1 = 完全显示。</summary>
        private void Apply(float progress)
        {
            if (group != null)
            {
                group.alpha = progress;
            }
            if (rect == null)
            {
                return;
            }
            // **滑动模式不缩放。**
            //
            // 两个一起做的话，缩放会把滑动盖过去 —— 位移在宽页面上几乎
            // 看不出来，而 0.96 的缩放很明显，观感就成了"在缩放"而不是
            // "在滑动"。用户要的是横向滑动，所以这里二选一。
            float scale = sliding ? 1f : Mathf.Lerp(HiddenScale, 1f, progress);
            rect.localScale = new Vector3(scale, scale, 1f);

            // **围绕中心缩放的补偿。**
            //
            // `localScale` 是围绕 **pivot** 缩的：pivot 那个点不动，其余部分
            // 按比例收缩。pivot 在 (0,1)（左上）时，视觉上就是从左上角缩放。
            //
            // 要让"视觉中心"保持不动，得把节点挪一下：
            //   中心相对 pivot 的偏移 = size * (0.5 - pivot)
            //   缩放后这个偏移被压成 scale 倍
            //   差值 = size * (0.5 - pivot) * (1 - scale)  ← 这就是补偿量
            //
            // **符号不能反。** pivot 在左上时 (0.5 - pivot) 的 x 是正的、
            // y 是负的，所以节点要往右下挪 —— 那正好抵消"向左上收缩"。
            // 写反了就会看着像从右下角缩放。
            float shrinkX = (1f - scale) * baseSize.x;
            float shrinkY = (1f - scale) * baseSize.y;
            Vector2 offset = new Vector2(
                shrinkX * (0.5f - basePivot.x),
                shrinkY * (basePivot.y - 0.5f));

            // **横向滑动：从 `slideDirection` 那一侧滑进来。**
            //
            // `progress` 0 时偏满一个 `SlideDistance`，1 时归位。
            // 方向 +1 表示"从右边来"，所以初始位置要往右偏。
            if (sliding)
            {
                offset.x += slideDirection * SlideDistance * (1f - progress);
            }

            rect.anchoredPosition = basePosition + offset;
        }
    }
}
