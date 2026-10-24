using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 给任意 uGUI 控件挂一条「鼠标附近显示名称」的提示。
    ///
    /// 工作台里按钮很多是纯图标（模式开关、车道筛选、播放控件），没有文字时
    /// 只能靠记忆。ImGui 那版用 `SetTooltip`；迁到 uGUI 之后没有等价物，
    /// 这里补一个最小的：进入时在浮层画布上放一块小标签，跟着鼠标走，离开就收起。
    ///
    /// **挂在浮层画布（32000）上**，不会被面板或 ImGui 层盖住。
    /// 只在需要时建一次，之后复用同一个实例。
    /// </summary>
    internal sealed class UiHoverTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal string Text;

        private static RectTransform layer;
        private static RectTransform body;
        private static TextMeshProUGUI label;
        private static bool visible;

        internal static void Attach(Component target, string text)
        {
            if (target == null)
            {
                return;
            }
            UiHoverTip tip = target.gameObject.GetComponent<UiHoverTip>();
            if (tip == null)
            {
                tip = target.gameObject.AddComponent<UiHoverTip>();
            }
            tip.Text = text;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            Show(Text);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Hide();
        }

        private void OnDisable()
        {
            Hide();
        }

        internal static void Show(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            EnsureBuilt();
            if (body == null || label == null)
            {
                return;
            }
            label.text = text;
            label.ForceMeshUpdate();
            Vector2 size = label.GetPreferredValues(text, 0f, 0f);
            body.sizeDelta = new Vector2(Mathf.Ceil(size.x) + 2f, 20f);
            label.rectTransform.sizeDelta = body.sizeDelta;
            body.gameObject.SetActive(true);
            visible = true;
            Follow();
        }

        internal static void Hide()
        {
            visible = false;
            if (body != null)
            {
                body.gameObject.SetActive(false);
            }
        }

        private static void EnsureBuilt()
        {
            if (body != null)
            {
                return;
            }
            EditorStudioOverlay overlay = Main.Overlay;
            if (overlay == null)
            {
                return;
            }
            layer = overlay.PopupLayer;
            if (layer == null)
            {
                return;
            }

            GameObject go = new GameObject("UiHoverTip", typeof(RectTransform));
            go.transform.SetParent(layer, false);
            body = go.GetComponent<RectTransform>();
            body.anchorMin = new Vector2(0f, 1f);
            body.anchorMax = new Vector2(0f, 1f);
            body.pivot = new Vector2(0f, 1f);
            body.sizeDelta = new Vector2(120f, 20f);

            // **不画底衬**：纯文字。深色底上挂一块半透明黑反而像另一个控件。
            GameObject textObject = new GameObject("Label", typeof(RectTransform));
            textObject.transform.SetParent(body, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            label = textObject.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset font = overlay.UiFont;
            if (font != null)
            {
                label.font = font;
            }
            label.fontSize = 12f;
            label.color = new Color(0.94f, 0.94f, 0.94f, 1f);
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;

            body.gameObject.SetActive(false);
        }

        /// <summary>跟着鼠标走 —— 每帧由这个组件自己驱动（挂在提示体上）。</summary>
        private void Update()
        {
            if (visible)
            {
                Follow();
            }
        }

        private static void Follow()
        {
            if (body == null || layer == null)
            {
                return;
            }
            Vector2 screen = Input.mousePosition;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                layer, screen, null, out local))
            {
                return;
            }
            // 局部坐标以中心为原点，这里换成「左上角为锚」。
            float width = body.sizeDelta.x;
            float height = body.sizeDelta.y;
            float x = local.x + layer.rect.width * 0.5f + 12f;
            float y = layer.rect.height * 0.5f - local.y + 6f;

            // **贴边时把提示拉回画布内。** 原来直接加偏移，靠近右/下边缘时
            // 长文本会被裁掉一截（用户看到的就是「边缘文字被剪」）。
            if (x + width > layer.rect.width - 4f)
            {
                x = Mathf.Max(4f, layer.rect.width - 4f - width);
            }
            if (x < 4f)
            {
                x = 4f;
            }
            if (y + height > layer.rect.height - 4f)
            {
                y = Mathf.Max(4f, layer.rect.height - 4f - height);
            }
            body.anchoredPosition = new Vector2(x, -y);
        }
    }
}
