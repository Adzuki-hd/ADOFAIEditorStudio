using ADOFAIEditorStudio.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio.Multiplayer
{
    /// <summary>
    /// 加入房间时的加载遮罩：**整块黑屏 + 一行字 + 一条进度条**。
    ///
    /// 存在的理由不是「好看」，是**遮住中间那些半成品状态**。
    /// 加入房间的过程里要重载好几次谱面（建会话副本、落地房主的谱面、
    /// 媒体收齐后再来一次），每一次都会让编辑器闪一下、装饰物少一半、
    /// 文字装饰物短暂错位。用户看到的就是「坏了」。
    ///
    /// 用它盖住之后，这些中间态都在黑屏底下发生，用户只看到一句
    /// 「下载谱面中 → 下载媒体 12/64」，结束才揭开。
    ///
    /// **布局全部走锚点，不用设计坐标。** canvas 用的是
    /// ScaleWithScreenSize + Expand，超宽屏下参考宽度会大于 1920，
    /// 那时候按 1920 铺的黑底会在两边留下缝。
    /// </summary>
    internal sealed class MultiplayerLoadingScreen : MonoBehaviour
    {
        private const float BarWidth = 420f;
        private const float BarHeight = 10f;

        private RectTransform root;
        private Image barFill;
        private TextMeshProUGUI label;
        private TMP_FontAsset font;
        private Sprite roundedSprite;

        internal void Initialize(RectTransform host, TMP_FontAsset uiFont, Sprite rounded)
        {
            root = host;
            font = uiFont;
            roundedSprite = rounded;
            Build();
            root.gameObject.SetActive(false);
        }

        private void Build()
        {
            // 纯黑满屏，并且吃掉点击——加载期间不该还能点到底下的东西。
            Image backdrop = CreateImage("Backdrop", root, new Color(0f, 0f, 0f, 1f));
            Stretch(backdrop.rectTransform);
            backdrop.raycastTarget = true;

            // 文字在屏幕中间偏上，进度条在它下面——视线先落在字上。
            label = CreateText("Label", root);
            PlaceCentered(label.rectTransform, 640f, 40f, 0f, 26f);
            label.text = string.Empty;
            label.fontSize = 20f;
            label.color = UiTheme.Current.TextPrimary;
            label.alignment = TextAlignmentOptions.Center;

            Image barBack = CreateImage("BarBack", root, new Color(1f, 1f, 1f, 0.12f));
            PlaceCentered(barBack.rectTransform, BarWidth, BarHeight, 0f, -22f);
            ApplyRounded(barBack, true);

            barFill = CreateImage("BarFill", root, UiTheme.Current.ActionPrimary);
            // 枢轴放左端，这样改宽度就是**从左往右**长，而不是两头一起长。
            PlaceCentered(barFill.rectTransform, BarWidth, BarHeight, 0f, -22f);
            barFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            barFill.rectTransform.anchoredPosition = new Vector2(-BarWidth * 0.5f, -22f);
            ApplyRounded(barFill, true);
        }

        /// <summary>
        /// 显示并更新。
        ///
        /// `progress` 小于 0 表示**还不知道进度**（比如刚开始等谱面），
        /// 那时条留空——文字已经说清了在做什么，画一条假进度只会误导。
        /// </summary>
        internal void Show(string text, float progress)
        {
            if (root == null)
            {
                return;
            }

            if (!root.gameObject.activeSelf)
            {
                root.gameObject.SetActive(true);
                root.SetAsLastSibling();
            }

            if (label != null)
            {
                label.text = text ?? string.Empty;
            }

            if (barFill != null)
            {
                float clamped = progress < 0f ? 0f : (progress > 1f ? 1f : progress);
                barFill.rectTransform.sizeDelta = new Vector2(BarWidth * clamped, BarHeight);
            }
        }

        internal void Hide()
        {
            if (root != null)
            {
                root.gameObject.SetActive(false);
            }
        }

        internal bool IsShowing
        {
            get { return root != null && root.gameObject.activeSelf; }
        }

        // ---------------------------------------------------------------- 布局与构造

        private void ApplyRounded(Image image, bool sliced)
        {
            if (roundedSprite == null || image == null)
            {
                return;
            }
            image.sprite = roundedSprite;
            image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
        }

        /// <summary>四边贴满父物体。背景用它，才能盖住任意宽高比。</summary>
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>以屏幕正中为原点摆放，`dy` 向上为正。</summary>
        private static void PlaceCentered(RectTransform rect, float width, float height,
            float dx, float dy)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(dx, dy);
        }

        private TextMeshProUGUI CreateText(string name, RectTransform parent)
        {
            RectTransform rect = CreateRect(name, parent);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            EditorStudioOverlay.ApplyTextShadow(text);
            return text;
        }

        private Image CreateImage(string name, RectTransform parent, Color color)
        {
            RectTransform rect = CreateRect(name, parent);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static RectTransform CreateRect(string name, RectTransform parent)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            return gameObject.GetComponent<RectTransform>();
        }
    }
}
