using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 让一个节点绕自己的中心转到目标角度，带 OutBack 弹性。
    ///
    /// **不用 DOTween。** 项目里虽然引了 `DG.Tweening`（游戏自带），但那是
    /// 谱面播放用的；UI 这边只有一个角度随时间变，自己算比配一套 tween 对象轻，
    /// 也不会和游戏的 tween 池抢资源（同 `Ui/UiTransition` 的做法）。
    ///
    /// **起点永远取当前的实际角度**，不是上一次的目标。连续快速开合时，
    /// 图标会从自己现在的位置接着转过去，而不是先跳回起点再开始 ——
    /// 那种跳法在动画中途打断时特别扎眼。
    /// </summary>
    internal sealed class UiArrowSpin : MonoBehaviour
    {
        private RectTransform target;

        private bool spinning;
        private float fromAngle;
        private float toAngle;
        private float elapsed;
        private float duration;

        /// <summary>
        /// OutBack 的过冲系数。
        ///
        /// 1.70158 是 Robert Penner 的经典取值，弹得比较明显。**这个图标只有
        /// 11px，过冲太猛会看着像抖了一下**，所以取 1.2 —— 还剩回弹的手感，
        /// 但不至于把三角甩出去。
        /// </summary>
        private const float Overshoot = 1.2f;

        /// <summary>挂到节点上（已经有了就复用）。</summary>
        internal static UiArrowSpin Attach(RectTransform node)
        {
            if (node == null)
            {
                return null;
            }
            UiArrowSpin spin = node.GetComponent<UiArrowSpin>();
            if (spin == null)
            {
                spin = node.gameObject.AddComponent<UiArrowSpin>();
            }
            spin.target = node;
            return spin;
        }

        /// <summary>
        /// 转到 <paramref name="targetAngle"/>（度，绕 Z 轴）。
        ///
        /// 调用前**必须**保证节点的 pivot 是 (0.5, 0.5)。pivot 在角上时
        /// `localEulerAngles` 会让节点绕着那个角甩，看起来像被甩出去而不是转。
        /// </summary>
        internal void SpinTo(float targetAngle, float seconds)
        {
            if (target == null)
            {
                return;
            }

            fromAngle = CurrentAngle();
            toAngle = targetAngle;
            elapsed = 0f;
            duration = Mathf.Max(0.01f, seconds);
            spinning = true;
        }

        /// <summary>直接摆到某个角度，不播动画。</summary>
        internal void SnapTo(float angle)
        {
            spinning = false;
            ApplyRaw(angle);
        }

        /// <summary>当前角度，归一化到 [-180, 180]。</summary>
        internal float CurrentAngle()
        {
            if (target == null)
            {
                return 0f;
            }
            return NormalizeAngle(target.localEulerAngles.z);
        }

        private void Update()
        {
            if (!spinning)
            {
                return;
            }
            if (target == null)
            {
                // 节点被销毁了（面板重建会把整棵 UI 拆掉）。
                // 这时候安静退出即可，不要接着算 —— 算下去就是 MissingReference。
                spinning = false;
                return;
            }

            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            // **用 unscaledDeltaTime。** 工作台可能停在暂停状态（timeScale = 0），
            // 那期间 UI 仍然要能动，否则点开下拉箭头是死的。
            float eased = OutBack(t);
            ApplyRaw(Mathf.LerpUnclamped(fromAngle, toAngle, eased));

            if (t >= 1f)
            {
                spinning = false;
                ApplyRaw(toAngle);
            }
        }

        private void OnDisable()
        {
            // 节点被禁用时停掉推进。`Update` 本来也不会跑，但把状态清掉更明确 ——
            // 重新启用时不会接着从旧进度算。
            spinning = false;
        }

        private void ApplyRaw(float angle)
        {
            if (target == null)
            {
                return;
            }
            target.localEulerAngles = new Vector3(0f, 0f, angle);
        }

        /// <summary>
        /// OutBack：先冲过目标一点，再弹回来。
        ///
        /// 公式来自 Robert Penner 的经典缓动。`eased` 在 t 接近 1 时会略大于 1，
        /// 那正是「冲过头」的部分；末尾再收回到 1。
        /// </summary>
        private static float OutBack(float t)
        {
            float c1 = Overshoot;
            float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f)
            {
                angle -= 360f;
            }
            else if (angle < -180f)
            {
                angle += 360f;
            }
            return angle;
        }
    }
}
