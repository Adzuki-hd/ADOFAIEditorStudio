using UnityEngine;
using UnityEngine.EventSystems;

namespace ADOFAIEditorStudio
{
    internal sealed class TimelineResizeHandle : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler,
        IPointerEnterHandler, IPointerExitHandler
    {
        private EditorStudioOverlay owner;
        private RectTransform timelinePanel;
        private RectTransform designRoot;
        private float dragStartPointerY;
        private float dragStartPanelHeight;
        private bool dragging;
        private bool hovered;

        internal void Initialize(EditorStudioOverlay overlay, RectTransform panel)
        {
            owner = overlay;
            timelinePanel = panel;
            designRoot = panel == null ? null : panel.parent as RectTransform;
        }

        // ── 光标 ─────────────────────────────────────────────────────────
        //
        // **悬停在整条顶边上就换成「上下双向箭头」。**
        //
        // 这条热区现在横贯整个时间轴宽度（见 `EditorTimelineView` 里
        // `ResizeHandle` 的建法），所以光标一进来就该告诉用户"这里能拖高度"。
        //
        // 指针纹理由 `EditorTimelineView.GetTimelineResizeCursorTexture` 生成并缓存
        // —— 那里踩过的坑（纹理必须 CPU 可读）写在该方法的注释里。

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
            ApplyCursor(true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovered = false;
            if (!dragging)
            {
                ApplyCursor(false);
            }
        }

        private void OnDisable()
        {
            hovered = false;
            if (!dragging)
            {
                ApplyCursor(false);
            }
        }

        /// <summary>换 / 还指针。拖动中保持箭头，松手后按悬停状态还原。</summary>
        private static void ApplyCursor(bool resize)
        {
            if (resize)
            {
                Texture2D texture = EditorTimelineView.GetTimelineResizeCursorTexture();
                if (texture != null)
                {
                    // 热点取正中心 —— 箭头上下对称，中心正好落在横杠上。
                    Cursor.SetCursor(texture,
                        new Vector2(texture.width * 0.5f, texture.height * 0.5f),
                        CursorMode.Auto);
                }
            }
            else
            {
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            }
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
            ApplyCursor(true);
            // 指针探针（设置 → 开发者，默认关）：记录拖顶边开始/结束时选中了几个
            // 装饰物。点穿如果发生在**按下那一刻**，这里的 before/after 会不一样。
            if (Main.Settings != null && Main.Settings.PointerProbe)
            {
                Main.Logger.Log("[HandleProbe] BeginDrag at " + eventData.position
                    + " selectedDecorations(before)=" + CountSelectedDecorations());
            }
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
            // 指针探针：见 `OnBeginDrag`。两边都有才看得出「拖这一下有没有动到选中」。
            if (Main.Settings != null && Main.Settings.PointerProbe)
            {
                Main.Logger.Log("[HandleProbe] EndDrag at " + eventData.position
                    + " selectedDecorations(after)=" + CountSelectedDecorations());
            }
            // 松手时鼠标可能已经移出热区，按悬停状态还原指针。
            if (!hovered)
            {
                ApplyCursor(false);
            }
            eventData.Use();
        }

        /// <summary>【临时探针】当前选中了几个装饰物。</summary>
        private static string CountSelectedDecorations()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.selectedDecorations == null)
            {
                return "?";
            }
            int count = 0;
            for (int i = 0; i < editor.selectedDecorations.Count; i++)
            {
                if (editor.selectedDecorations[i] != null)
                {
                    count++;
                }
            }
            return count.ToString();
        }
    }
}
