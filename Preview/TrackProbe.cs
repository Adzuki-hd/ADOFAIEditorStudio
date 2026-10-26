using System.Collections.Generic;
using System.Text;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview
{
    /// <summary>
    /// 「轨道不见了」的排查探针。
    ///
    /// 它只回答两个问题，因为这两件事的修法完全不同：
    ///   1. 轨道是**没画**——渲染器关着、被禁用、颜色 alpha 掉到 0、
    ///      排序层被改掉；
    ///   2. 还是**在屏幕外**——渲染器正常，但相机被 MoveCamera 甩走了，
    ///      屏幕坐标落在视口之外（或者 z 为负，在相机背后）。
    ///
    /// 每 0.5 秒写一行。探针只在设置 → 开发者里手动打开时工作，
    /// 平时不产生日志。
    /// </summary>
    internal static class TrackProbe
    {
        private static float lastLoggedTime = float.NaN;
        private static bool loggedSortingLayers;

        /// <summary>
        /// 固定采样的几格：本谱（杪夏）的关键节点。214 是「轨道消失」的
        /// 起点，88 / 136 是之前两条 RecolorTrack，230 是相机回程附近。
        /// </summary>
        private static readonly int[] FixedFloors = { 0, 88, 136, 214, 215, 220, 230, 300 };

        private static readonly List<int> floorsToLog = new List<int>(12);

        internal static void Tick(PreviewEvaluationFrame frame, scrCamera camera)
        {
            if (Main.Settings == null || !Main.Settings.TrackProbe)
            {
                return;
            }
            if (frame == null || frame.Context == null || camera == null || camera.camobj == null)
            {
                return;
            }

            float time = frame.Time;
            // 时间没动（暂停在某一格）时也要在第一次进来时写一条，
            // 所以只在「已经写过且时间几乎没变」时才跳过。
            if (!float.IsNaN(lastLoggedTime) && Mathf.Abs(time - lastLoggedTime) < 0.5f)
            {
                return;
            }
            lastLoggedTime = time;

            Camera unityCamera = camera.camobj;
            Vector3 cameraPosition = unityCamera.transform.position;
            int currentFloor = frame.Context.FindFloorAtOrBefore(time);

            StringBuilder builder = new StringBuilder(640);
            builder.Append("[TrackProbe] t=").Append(time.ToString("0.000"))
                .Append(" f=").Append(currentFloor)
                .Append(" cam=").Append(cameraPosition.x.ToString("0.00"))
                .Append(',').Append(cameraPosition.y.ToString("0.00"))
                .Append(" ortho=").Append(unityCamera.orthographicSize.ToString("0.000"))
                .Append(" zoom=").Append(camera.zoomSize.ToString("0.000"))
                .Append(" mask=").Append(unityCamera.cullingMask)
                .Append(" clear=").Append(unityCamera.clearFlags)
                .Append(" depth=").Append(unityCamera.depth)
                .Append(" bgDepth=").Append(camera.BGcam == null ? -999 : camera.BGcam.depth)
                .Append(" bgClear=").Append(camera.BGcam == null ? "-" : camera.BGcam.clearFlags.ToString())
                .Append(" staticDepth=").Append(camera.Bgcamstatic == null ? -999 : camera.Bgcamstatic.depth)
                .Append(" staticClear=").Append(camera.Bgcamstatic == null ? "-" : camera.Bgcamstatic.clearFlags.ToString());

            scnGame customLevel = ADOBase.customLevel;
            if (customLevel != null && customLevel.custBG != null
                && customLevel.custBG.displayedSprite != null)
            {
                SpriteRenderer backgroundSprite = customLevel.custBG.displayedSprite;
                builder.Append(" bgSprite=").Append(backgroundSprite.enabled ? 1 : 0)
                    .Append(" bgSort=").Append(backgroundSprite.sortingLayerName)
                    .Append(':').Append(backgroundSprite.sortingOrder)
                    .Append(" bgA=").Append(backgroundSprite.color.a.ToString("0.00"))
                    .Append(" bgLayer=").Append(backgroundSprite.gameObject.layer);
            }

            floorsToLog.Clear();
            AddFloor(currentFloor - 2);
            AddFloor(currentFloor);
            AddFloor(currentFloor + 2);
            for (int i = 0; i < FixedFloors.Length; i++)
            {
                AddFloor(FixedFloors[i]);
            }

            for (int i = 0; i < floorsToLog.Count; i++)
            {
                AppendFloor(builder, frame.Context, unityCamera, floorsToLog[i]);
            }

            if (!loggedSortingLayers)
            {
                loggedSortingLayers = true;
                SortingLayer[] layers = SortingLayer.layers;
                builder.Append(" | layers=");
                for (int i = 0; i < layers.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }
                    builder.Append(layers[i].name);
                }
            }

            Main.Logger.Log(builder.ToString());
        }

        private static void AddFloor(int index)
        {
            if (index < 0 || floorsToLog.Contains(index))
            {
                return;
            }
            floorsToLog.Add(index);
        }

        private static void AppendFloor(StringBuilder builder, PreviewTimelineContext context,
            Camera camera, int index)
        {
            scrFloor floor = context.GetFloorBinding(index);
            if (floor == null)
            {
                builder.Append(" | f").Append(index).Append("=null");
                return;
            }

            FloorRenderer floorRenderer = floor.floorRenderer;
            Renderer renderer = floorRenderer == null ? null : floorRenderer.renderer;
            Vector3 world = floor.transform.position;
            Vector3 screen = camera.WorldToScreenPoint(world);

            builder.Append(" | f").Append(index)
                .Append(" vis=").Append(renderer != null && renderer.isVisible ? 1 : 0)
                .Append(" en=").Append(renderer != null && renderer.enabled ? 1 : 0)
                .Append(" act=").Append(floor.gameObject.activeInHierarchy ? 1 : 0)
                .Append(" opa=").Append(floor.opacity.ToString("0.00"))
                .Append(" base=").Append(floor.opacityVal.ToString("0.00"))
                .Append(" col=").Append(floorRenderer == null ? "-" : ToHex(floorRenderer.color))
                .Append(" sort=").Append(renderer == null
                    ? "-"
                    : renderer.sortingLayerName + ":" + renderer.sortingOrder)
                .Append(" pos=").Append(world.x.ToString("0.0"))
                .Append(',').Append(world.y.ToString("0.0"))
                .Append(" scr=").Append(screen.x.ToString("0"))
                .Append(',').Append(screen.y.ToString("0"))
                .Append(',').Append(screen.z.ToString("0.0"));
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
