using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 说明窗口右边那条滚动条：**能左键拖**。
    ///
    /// 滚轮归 <see cref="ScrollRect"/> 管，这条只管"抓着手柄拖"那一手 ——
    /// 长文档光靠滚轮翻很累，得有个能一把拖到底的东西。
    ///
    /// 装不下时才出现：内容比视口矮的时候把它自己藏掉，免得留着一条
    /// 拖不动的东西让人以为坏了。
    /// </summary>
    internal sealed class EditorHelpScroll : MonoBehaviour,
        IPointerDownHandler, IBeginDragHandler, IDragHandler
    {
        internal ScrollRect Scroll;
        internal RectTransform Track;
        internal RectTransform Handle;

        private void LateUpdate()
        {
            Refresh();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            Drag(eventData);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            Drag(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            Drag(eventData);
        }

        private void Refresh()
        {
            if (Scroll == null || Track == null || Handle == null
                || Scroll.viewport == null || Scroll.content == null)
            {
                return;
            }

            float viewHeight = Scroll.viewport.rect.height;
            float contentHeight = Scroll.content.rect.height;
            float trackHeight = Track.rect.height;
            if (trackHeight < 1f)
            {
                return;
            }

            // 装得下就整条藏掉。留着一条拖不动的槽，看着像"这里能滚但不灵"。
            bool needed = contentHeight > viewHeight + 1f;
            if (Track.gameObject.activeSelf != needed)
            {
                Track.gameObject.SetActive(needed);
            }
            if (!needed)
            {
                return;
            }

            float handleHeight = Mathf.Clamp(trackHeight * viewHeight / contentHeight, 28f, trackHeight);
            // verticalNormalizedPosition：1 = 在最顶，0 = 在最底。
            float top = (1f - Scroll.verticalNormalizedPosition) * (trackHeight - handleHeight);
            SetTopLeft(Handle, 0f, top, Track.rect.width, handleHeight);
        }

        private void Drag(PointerEventData eventData)
        {
            if (Scroll == null || Track == null || Handle == null)
            {
                return;
            }

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    Track, eventData.position, eventData.pressEventCamera, out local))
            {
                return;
            }

            float trackHeight = Track.rect.height;
            float handleHeight = Handle.rect.height;
            float travel = trackHeight - handleHeight;
            if (travel <= 0f)
            {
                return;
            }

            // 让手柄**中心**落在指针上 —— 按下那一下也算，不然点一下会先跳一段
            // 再跟手。坐标是"距轨道顶边多少"：轨道是左上锚 + 左上轴心，
            // 所以 local.y 在 0..-高 之间，取负就是从顶边算的距离。
            float fromTop = -local.y - handleHeight * 0.5f;
            float t = Mathf.Clamp01(fromTop / travel);
            Scroll.verticalNormalizedPosition = 1f - t;
            eventData.Use();
        }

        private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }

    /// <summary>
    /// 把说明正文的高度按文本量重算。
    ///
    /// **这是"滚轮滚不动"的根因所在。** TMP 的文字排版要等一帧才准，而构建
    /// 那一刻去问 `GetPreferredValues` 拿到的是 0 —— 内容高度于是等于视口高度，
    /// <see cref="ScrollRect"/> 认为没东西可滚，滚轮自然一点反应都没有。
    ///
    /// 所以进来之后补算几帧就够（内容不会自己变），不必每帧都算 ——
    /// 对一整篇文本反复问 preferredHeight 不便宜。
    /// </summary>
    internal sealed class HelpContentSizer : MonoBehaviour
    {
        internal TextMeshProUGUI Body;
        internal RectTransform Viewport;
        internal RectTransform Content;

        private int settled;

        private void OnEnable()
        {
            // 每次重新打开都重来一轮：工作台重建、分辨率变化之后，
            // 视口宽度变了，换行位置也跟着变。
            settled = 0;
            Resize();
        }

        private void LateUpdate()
        {
            // 补算**半秒左右**就停。等一帧通常就够，但工作台刚建出来、
            // 分辨率刚变过的那几帧里视口宽度可能还是 0，量不出东西 ——
            // 只给两三帧的话会正好撞上那几帧，然后就此定型（内容高度永远
            // 等于视口高，滚不动）。
            if (settled > 30)
            {
                return;
            }
            settled++;
            Resize();
        }

        private void Resize()
        {
            if (Body == null || Viewport == null || Content == null)
            {
                return;
            }

            float width = Viewport.rect.width;
            if (width < 1f || Body.text == null)
            {
                return;
            }

            float textHeight = Body.GetPreferredValues(Body.text, width, 0f).y + 12f;
            float height = Mathf.Max(Viewport.rect.height, textHeight);

            Content.sizeDelta = new Vector2(Content.sizeDelta.x, height);
            Body.rectTransform.sizeDelta = new Vector2(width, height);
        }
    }
}
