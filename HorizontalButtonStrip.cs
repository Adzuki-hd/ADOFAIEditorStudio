using UnityEngine;
using UnityEngine.EventSystems;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 一条横向的按钮带：内容排不下时可以滚。
    ///
    /// **滚轮和左键拖动都能滚**，这是需求里点名的两条。
    ///
    /// 没用 `ScrollRect` 就是冲着滚轮去的：它横向模式只吃**横向**的滚轮分量
    /// （`scrollDelta.x`），而玩家滚的是竖向滚轮（`scrollDelta.y`），落在横向
    /// ScrollRect 上会被整个忽略 —— 表现就是"滚了没反应"。与其去覆写它的
    /// `OnScroll`，不如自己收这两件事，少一层要理解的东西。
    ///
    /// 它自己不建任何东西：内容由调用方建好挂进来，这里只负责挪。
    /// 裁剪靠宿主上的 `RectMask2D`，没有那个就等于没滚 —— 内容会直接画到外面。
    /// </summary>
    internal sealed class HorizontalButtonStrip : MonoBehaviour,
        IScrollHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>滚轮一格挪多少像素。</summary>
        private const float WheelStep = 56f;

        private RectTransform content;
        private float offset;
        private float viewWidth;
        private float contentWidth;

        internal void Configure(RectTransform contentRect)
        {
            content = contentRect;
            offset = 0f;
            Apply();
        }

        /// <summary>告诉它「看得见多宽」和「内容一共多宽」，然后夹一次。</summary>
        internal void SetBounds(float viewportWidth, float totalContentWidth)
        {
            viewWidth = Mathf.Max(0f, viewportWidth);
            contentWidth = Mathf.Max(0f, totalContentWidth);
            ClampOffset();
            Apply();
        }

        /// <summary>把内容挪回最左。换了一页之后带子不该还停在中间。</summary>
        internal void ResetToStart()
        {
            offset = 0f;
            Apply();
        }

        public void OnScroll(PointerEventData eventData)
        {
            // 竖向滚轮当横向用：往上滚内容往右走，和列表的习惯一致 ——
            // 手指往上推，看到的是右边更多的东西。
            offset -= eventData.scrollDelta.y * WheelStep;
            ClampOffset();
            Apply();
            eventData.Use();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            // 拖拽目标由按下点决定，而按钮自己只实现 IPointerDownHandler，
            // 不实现 IDragHandler，所以从按钮上按下时这里照样收得到 ——
            // 也就是说"按在按钮上直接拖"就能滚，不用先找缝。
        }

        public void OnDrag(PointerEventData eventData)
        {
            // 用 delta 而不是位置差：delta 已经过缩放，和屏幕像素同量纲。
            offset -= eventData.delta.x;
            ClampOffset();
            Apply();
            eventData.Use();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            // 拖过就算滚动，那次点击作废。不作废的话，松手会顺手按下一个按钮 ——
            // 想滚到右边，结果每滚一次就切一个页。
            eventData.eligibleForClick = false;
        }

        private void ClampOffset()
        {
            float max = Mathf.Max(0f, contentWidth - viewWidth);
            offset = Mathf.Clamp(offset, 0f, max);
        }

        private void Apply()
        {
            if (content == null)
            {
                return;
            }
            content.anchoredPosition = new Vector2(-offset, 0f);
        }
    }
}
