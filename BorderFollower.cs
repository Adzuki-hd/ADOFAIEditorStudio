using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 面板尺寸变化时，保持"垫在面板背后的那一圈"尺寸正确。
    ///
    /// 这一圈是独立于面板定尺寸的对象，所以面板一变尺寸——
    /// 拖动时间轴把手，或窗口缩放导致任意面板变尺寸——
    /// 它就留在旧尺寸上：太大则在面板后面露出一圈，太小则被面板裁掉。
    /// 放在 LateUpdate 里跟随，而不是让每条布局路径各自推送尺寸，
    /// 这样所有调用方都白拿这份好处，包括那些挪动面板却根本
    /// 不知道有这一圈存在的调用方。
    ///
    /// 描边和阴影共用这个组件：两者都是"比面板大 margin、
    /// 且随面板尺寸变化"的圆角矩形，只是偏移不同。
    /// </summary>
    internal sealed class BorderFollower : MonoBehaviour
    {
        internal RectTransform Target;
        internal float Margin;

        // 这一圈相对面板的偏移（右, 上）。描边为 (0, 0)，四边均匀；
        // 阴影用正数往下、往右推开，就成了投影。
        // 单位是面板自身的坐标，所以 +y 是往上。
        internal Vector2 Offset;

        private RectTransform rect;

        private void Awake()
        {
            rect = GetComponent<RectTransform>();
        }

        private void LateUpdate()
        {
            if (rect == null || Target == null)
            {
                return;
            }

            Vector2 wanted = Target.sizeDelta + new Vector2(Margin * 2f, Margin * 2f);
            if (rect.sizeDelta != wanted)
            {
                rect.sizeDelta = wanted;
            }

            // 左上角 pivot 坐标系：anchoredPosition 是 (右, 下)，
            // 所以这一圈在向右下变大的同时，位置要向左上移动。
            // Offset 的 +y 表示"往上"，换算到本坐标系是负的 y。
            Vector2 position = Target.anchoredPosition
                - new Vector2(Margin, -Margin)
                + new Vector2(Offset.x, -Offset.y);
            if (rect.anchoredPosition != position)
            {
                rect.anchoredPosition = position;
            }
        }
    }
}
