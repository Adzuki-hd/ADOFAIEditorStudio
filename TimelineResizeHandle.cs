using UnityEngine;
using UnityEngine.EventSystems;

namespace ADOFAIEditorStudio
{
    internal sealed class TimelineResizeHandle : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private EditorStudioOverlay owner;
        private RectTransform timelinePanel;
        private RectTransform designRoot;
        private float dragStartPointerY;
        private float dragStartPanelHeight;
        private bool dragging;

        internal void Initialize(EditorStudioOverlay overlay, RectTransform panel)
        {
            owner = overlay;
            timelinePanel = panel;
            designRoot = panel == null ? null : panel.parent as RectTransform;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                // 手柄是时间轴 root 的子物体。没有这句时，PointerDown 会继续冒泡到
                // EditorTimelineView.OnPointerDown，被它当成"点空白处"而清空选中；
                // BeginDrag 之后虽然进了这里，选中早就没了。
                eventData.Use();
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left
                || owner == null || timelinePanel == null || designRoot == null)
            {
                return;
            }

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    designRoot,
                    eventData.position,
                    eventData.pressEventCamera,
                    out local))
            {
                return;
            }

            dragStartPointerY = local.y;
            dragStartPanelHeight = timelinePanel.rect.height;
            dragging = true;
            eventData.Use();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || owner == null || designRoot == null)
            {
                return;
            }

            Vector2 local;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    designRoot,
                    eventData.position,
                    eventData.pressEventCamera,
                    out local))
            {
                owner.SetTimelinePanelHeight(dragStartPanelHeight + local.y - dragStartPointerY, false);
                eventData.Use();
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            if (owner != null)
            {
                owner.SetTimelinePanelHeight(owner.CurrentTimelinePanelHeight, true);
            }
            eventData.Use();
        }
    }
}
