using System;
using System.Collections.Generic;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    /// <summary>
    /// freeroam（自由移动）段的**出场动画**。
    ///
    /// 为什么单独一个域：freeroam 的网格格子不是关卡的主轨道格——它们住在
    /// scrLevelMaker.listFreeroam 里，而求值器的快照来自 listFloors
    /// （scnEditor.floors 就是它）。所以那批格子进不了任何按格驱动的域，
    /// 只能在这里直接拿它们来动。
    ///
    /// 原生做这件事的是 scrController.UpdateFreeroam（28867），触发条件是
    /// `songposition > nextfloor.entryTime - (crotchet/speed) * outTime`——
    /// 也就是**结束前 outTime 拍**开始：
    ///
    ///   foreach (tile in listFreeroam[section]) {
    ///       if (tile != 球所在的那格) { tile.isLandable = false; tile.TweenOpacity(0, crotchet); }
    ///       else {
    ///           endValue = nextfloor.position - tileSize * next.radiusScale
    ///                                        * (sin(exitangle), cos(exitangle));
    ///           duration = (crotchet/speed) * outTime * 0.5f / pitch;
    ///           tile.transform.DOMove(endValue, duration).SetEase(outEase);
    ///           if (outCam) MoveCameraToTile(nextfloor, tile, duration, outEase);
    ///           再等 outTime 拍之后 tile.TweenOpacity(0, crotchet)
    ///       }
    ///   }
    ///
    /// 球是**骑着那格走**的：原生在 stickToFloors 时不单独给球起 tween，
    /// 而是靠 LateUpdate 把它钉在 currfloor.position 上。所以这里只要把那格
    /// 挪对，球自然跟上——行星域通过 BallTilePosition 读它。
    ///
    /// 原版那几条是 DOTween（按真实秒推进）；这里一律按播放头解析地算，
    /// 拖到任意位置都能得到同一帧，和本域其它地方的做法一致。
    /// </summary>
    internal sealed class FreeRoamPreviewDomain : IPreviewDomain
    {
        public string Name
        {
            get { return "FreeRoam"; }
        }

        /// <summary>球所在那格此刻的位置，行星域在 freeroam 段用它当圆心。</summary>
        internal static Vector3 BallTilePosition;

        /// <summary>上面那个值这一帧有没有准备好。</summary>
        internal static bool BallTileValid;

        /// <summary>出场运镜。相机域读它，active 为假时走平常的跟随。
        /// Progress 是已经过 Ease 的 0..1 值。</summary>
        internal static bool ExitCameraActive;
        internal static Vector2 ExitCameraFrom;
        internal static Vector2 ExitCameraTo;
        internal static float ExitCameraProgress;

        /// <summary>每段的"球那格"出场前的位置。必须在任何人动它之前抓好，
        /// 否则下一帧读到的就是自己上一帧写进去的中间值。</summary>
        private static Vector3[] ballTileOrigin = new Vector3[0];

        public void Analyze(PreviewTimelineContext context)
        {
            BallTileValid = false;
            ExitCameraActive = false;

            scrLevelMaker maker = scrLevelMaker.instance;
            if (maker == null || maker.listFreeroamStartTiles == null
                || maker.listFreeroam == null)
            {
                ballTileOrigin = new Vector3[0];
                return;
            }

            int count = Mathf.Min(maker.listFreeroamStartTiles.Count, maker.listFreeroam.Count);
            ballTileOrigin = new Vector3[count];
            for (int s = 0; s < count; s++)
            {
                // 网格 (0,0) 就是 MakeFreeroamGrid 里 i=0 建的那颗，摆在入口格
                // 的位置再加 positionOffset。出场前它一直不动，所以现在抓最准。
                FreeroamArea tiles = maker.listFreeroam[s];
                if (tiles != null && tiles.Count > 0 && tiles[0] != null)
                {
                    ballTileOrigin[s] = tiles[0].transform.position;
                }
            }

            Main.Logger.Log("[StaticPreview/FreeRoam] " + count + " section(s).");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            BallTileValid = false;
            ExitCameraActive = false;

            scrLevelMaker maker = scrLevelMaker.instance;
            scrConductor conductor = scrConductor.instance;
            if (maker == null || conductor == null || conductor.song == null
                || maker.listFreeroamStartTiles == null || maker.listFreeroam == null)
            {
                return;
            }

            float pitch = conductor.song.pitch <= 0f ? 1f : conductor.song.pitch;
            int count = Mathf.Min(maker.listFreeroamStartTiles.Count, maker.listFreeroam.Count);
            if (ballTileOrigin.Length < count)
            {
                return;
            }

            for (int s = 0; s < count; s++)
            {
                scrFloor entry = maker.listFreeroamStartTiles[s];
                FreeroamArea tiles = maker.listFreeroam[s];
                if (entry == null || entry.nextfloor == null || tiles == null || tiles.Count == 0)
                {
                    continue;
                }

                if (frame.Time < entry.entryTime || frame.Time >= entry.nextfloor.entryTime)
                {
                    continue;
                }

                DriveSection(entry, tiles, ballTileOrigin[s], frame.Time, conductor, pitch,
                    frame.Context.TileSize);
            }
        }

        public void EndSession()
        {
            // 把网格格子放回出场前的位置、透明度还原。不还原的话，关掉工作台
            // 之后编辑器里那一片网格会留着被挪走 / 半透明的样子。
            scrLevelMaker maker = scrLevelMaker.instance;
            if (maker != null && maker.listFreeroam != null
                && maker.listFreeroamStartTiles != null)
            {
                int count = Mathf.Min(maker.listFreeroam.Count, ballTileOrigin.Length);
                for (int s = 0; s < count; s++)
                {
                    FreeroamArea tiles = maker.listFreeroam[s];
                    if (tiles == null || tiles.Count == 0 || tiles[0] == null)
                    {
                        continue;
                    }

                    tiles[0].transform.position = ballTileOrigin[s];
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        if (tiles[i] != null)
                        {
                            tiles[i].SetOpacity(1f);
                        }
                    }
                }
            }

            BallTileValid = false;
            ExitCameraActive = false;
        }

        private static void DriveSection(
            scrFloor entry, FreeroamArea tiles, Vector3 origin, float time,
            scrConductor conductor, float pitch, float tileSize)
        {
            scrFloor ballTile = tiles[0];
            if (ballTile == null)
            {
                return;
            }

            float speed = entry.speed <= 0f ? 1f : entry.speed;
            // 原生这条除了 speed 一次，照抄（crotchetAtStart 本身已经带过
            // 一次 speed，那是原生自己的写法）。
            float crotchet = (float)(conductor.crotchetAtStart / speed);
            if (crotchet <= 0f)
            {
                return;
            }

            int outBeats = entry.freeroamEndEarlyBeats;
            double exitStart = entry.nextfloor.entryTime - (double)crotchet * outBeats;
            bool exiting = outBeats > 0 && time >= exitStart;

            if (!exiting)
            {
                // 还没到出场：格子保持原样（ColorFreeroam 已经上过色）。
                ballTile.transform.position = origin;
                ballTile.SetOpacity(1f);
                BallTilePosition = origin;
                BallTileValid = true;
                return;
            }

            float edge = tileSize * entry.nextfloor.radiusScale;
            Vector3 endValue = entry.nextfloor.transform.position
                - new Vector3(
                    (float)Math.Sin(entry.exitangle) * edge,
                    (float)Math.Cos(entry.exitangle) * edge,
                    0f);

            float moveDuration = crotchet * outBeats * 0.5f / pitch;
            float moveRatio = moveDuration <= 0.0001f
                ? 1f
                : Mathf.Clamp01((float)(time - exitStart) / moveDuration);
            float moveEased = PreviewEase.EasedValue(0f, 1f, moveRatio, entry.freeroamEndEase);

            // 其它格子：出场一开始就淡，用一拍。
            float otherFade = Mathf.Clamp01((float)(time - exitStart) / crotchet);
            // 球那格：挪到位之后再淡，也是一拍。
            float ballFadeStart = crotchet * outBeats / pitch;
            float ballFade = Mathf.Clamp01(
                (float)(time - exitStart - ballFadeStart) / crotchet);

            for (int i = 0; i < tiles.Count; i++)
            {
                scrFloor tile = tiles[i];
                if (tile == null)
                {
                    continue;
                }

                // 用原生的 SetOpacity（它写 opacity 再 ApplyColor），和本域其它
                // 地方驱动格透明度走的是同一条，别直接写字段。
                tile.SetOpacity(tile == ballTile ? 1f - ballFade : 1f - otherFade);
            }

            ballTile.transform.position = Vector3.LerpUnclamped(origin, endValue, moveEased);
            BallTilePosition = ballTile.transform.position;
            BallTileValid = true;

            // 原生 MoveCameraToTile 是 Tile 模式：相机贴着那格平移，终点是下一格。
            if (entry.moveCamAtFreeroamEnd)
            {
                ExitCameraActive = true;
                ExitCameraFrom = new Vector2(origin.x, origin.y);
                ExitCameraTo = new Vector2(
                    entry.nextfloor.transform.position.x,
                    entry.nextfloor.transform.position.y);
                ExitCameraProgress = moveEased;
            }
        }
    }
}
