using System.Text;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// F9：把**当前活着的那一份**装饰物状态整表写进日志。
    ///
    /// 它不经过求值器，所以同一段代码可以当两个探针用：
    ///   · 关掉工作台、原版编辑器（自动播放）停在 214 按 F9 → 记的是原版；
    ///   · 工作台预览停在 214 按 F9 → 记的是 AES。
    /// 两边字段逐项对齐（tag / 图片 / 透明度 / 颜色 / 缩放 / 深度 /
    /// 排序层 / 屏幕矩形 / 遮罩 / 渲染器开关），可以直接 diff，
    /// 不需要靠肉眼猜「谁盖住了谁」。
    ///
    /// 只在设置 → 开发者 → 轨道探针打开时响应，避免误触。
    /// </summary>
    internal static class DecorationLiveDump
    {
        internal static void Tick()
        {
            if (Main.Settings == null || !Main.Settings.TrackProbe)
            {
                return;
            }
            if (!Input.GetKeyDown(KeyCode.F9))
            {
                return;
            }
            Dump();
        }

        private static void Dump()
        {
            scrDecorationManager manager = scrDecorationManager.instance;
            if (manager == null || manager.allDecorations == null)
            {
                Main.Logger.Log("[DecoLive] 装饰物管理器还没起来，导出失败。");
                return;
            }

            Camera camera = scrCamera.instance == null ? null : scrCamera.instance.camobj;
            scnEditor editor = scnEditor.instance;
            bool inEditor = editor != null;
            int floor = inEditor ? editor.selectedFloors == null || editor.selectedFloors.Count == 0
                ? -1
                : editor.selectedFloors[0].seqID
                : -1;

            Main.Logger.Log("[DecoLive] BEGIN count=" + manager.allDecorations.Count
                + " scene=" + (inEditor ? "editor" : "play")
                + " floor=" + floor
                + " time=" + Time.time.ToString("0.000")
                + " camera=" + (camera == null ? "-" : camera.transform.position.x.ToString("0.00")
                    + "," + camera.transform.position.y.ToString("0.00")));

            for (int i = 0; i < manager.allDecorations.Count; i++)
            {
                scrDecoration decoration = manager.allDecorations[i];
                if (decoration == null)
                {
                    continue;
                }
                scrVisualDecoration visual = decoration as scrVisualDecoration;
                SpriteRenderer renderer = visual == null ? null : visual.spriteRenderer;
                SpriteAlphaMask spriteMask = visual == null ? null : visual.spriteAlphaMask;

                LevelEvent source = decoration.sourceLevelEvent;
                string tag = decoration.decorationTag ?? (source == null ? "?" : ReadString(source, "tag"));
                string image = source == null ? "?" : ReadString(source, "decorationImage");
                int authoredDepth = source == null ? 0 : ReadInt(source, "depth");

                Vector3 position = decoration.transform.position;
                Vector3 scale = decoration.transform.lossyScale;
                Color color = decoration.color;
                string screenRect = "-";
                if (renderer != null && camera != null)
                {
                    Bounds bounds = renderer.bounds;
                    Vector3 minimum = camera.WorldToScreenPoint(bounds.min);
                    Vector3 maximum = camera.WorldToScreenPoint(bounds.max);
                    screenRect = minimum.x.ToString("0") + "," + minimum.y.ToString("0")
                        + "~" + maximum.x.ToString("0") + "," + maximum.y.ToString("0");
                }

                Main.Logger.Log("[DecoLive] #" + i
                    + " tag=" + tag
                    + " image=" + image
                    + " floor=" + (source == null ? -1 : source.floor)
                    + " opa=" + decoration.opacity.ToString("0.00")
                    + " col=" + ToHex(color)
                    + " scale=" + scale.x.ToString("0.00") + "x" + scale.y.ToString("0.00")
                    + " depth=" + authoredDepth
                    + " pos=" + position.x.ToString("0.00") + "," + position.y.ToString("0.00")
                    + " sort=" + (renderer == null ? "-"
                        : renderer.sortingLayerName + ":" + renderer.sortingOrder)
                    + " rect=" + screenRect
                    + " comp(active=" + (decoration.gameObject != null
                        && decoration.gameObject.activeInHierarchy)
                    + " renderer=" + (renderer == null ? "无" : (renderer.enabled ? "开" : "关"))
                    + " sprite=" + (renderer == null || renderer.sprite == null
                        ? "null" : renderer.sprite.name)
                    + " maskInteraction=" + (renderer == null ? "-" : renderer.maskInteraction.ToString())
                    + " mesh=" + (visual != null && visual.meshRendererObj != null
                        && visual.meshRendererObj.activeSelf ? "画" : "关")
                    + " spriteMask=" + (spriteMask == null ? "无" : (spriteMask.enabled ? "开" : "关"))
                    + ").");
            }

            Main.Logger.Log("[DecoLive] END");
        }

        private static string ReadString(LevelEvent source, string key)
        {
            try
            {
                return source.GetString(key) ?? string.Empty;
            }
            catch
            {
                return "?";
            }
        }

        private static int ReadInt(LevelEvent source, string key)
        {
            try
            {
                return source.GetInt(key);
            }
            catch
            {
                return 0;
            }
        }

        private static string ToHex(Color color)
        {
            return ((int)(Mathf.Clamp01(color.r) * 255f)).ToString("x2")
                + ((int)(Mathf.Clamp01(color.g) * 255f)).ToString("x2")
                + ((int)(Mathf.Clamp01(color.b) * 255f)).ToString("x2")
                + ((int)(Mathf.Clamp01(color.a) * 255f)).ToString("x2");
        }
    }
}
