using System;
using System.Collections.Generic;
using ADOFAI;
using UnityEngine;

namespace ADOFAIEditorStudio.Preview.Domains
{
    internal sealed class CustomBackgroundPreviewDomain : IPreviewDomain
    {
        private sealed class BackgroundKeyframe : IPreviewTimedKeyframe
        {
            internal double StartTime;
            internal Color Color;
            internal string Image;
            internal Color ImageColor;
            internal Vector2 Parallax;
            internal BgDisplayMode DisplayMode;
            internal bool ImageSmoothing;
            internal bool LockRotation;
            internal bool Loop;
            internal float ScalingRatio;

            public double Start
            {
                get { return StartTime; }
            }
        }

        private readonly List<BackgroundKeyframe> keyframes = new List<BackgroundKeyframe>();
        private BackgroundKeyframe defaultState;
        private int lastAppliedIndex = int.MinValue;

        /// <summary>
        /// 已经试着从磁盘读过、但没读出来的背景图文件名。
        ///
        /// Apply 是个失败就返回 false 的方法，而 false 意味着不推进
        /// lastAppliedIndex —— 于是每帧都会重进一次。这张表让“读不出来”
        /// 只花一次 IO。换图（文件名变了）时会重新试；EndSession 时清空。
        /// </summary>
        private static readonly HashSet<string> attemptedLoads = new HashSet<string>();

        public string Name
        {
            get { return "CustomBackground"; }
        }

        public void Analyze(PreviewTimelineContext context)
        {
            keyframes.Clear();
            lastAppliedIndex = int.MinValue;
            LevelData levelData = scnGame.instance == null ? null : scnGame.instance.levelData;
            defaultState = CreateDefault(levelData);

            IList<PreviewEventSnapshot> events = context.GetEvents(LevelEventType.CustomBackground);
            for (int i = 0; i < events.Count; i++)
            {
                PreviewEventSnapshot levelEvent = events[i];

                keyframes.Add(new BackgroundKeyframe
                {
                    StartTime = context.GetEventStartTime(levelEvent),
                    Color = ReadColor(levelEvent, "color", defaultState.Color),
                    Image = context.GetValue(levelEvent, "bgImage", string.Empty),
                    ImageColor = ReadColor(levelEvent, "imageColor", Color.white),
                    Parallax = context.GetValue(levelEvent, "parallax", Vector2.zero) / 100f,
                    DisplayMode = context.GetValue(levelEvent, "bgDisplayMode", BgDisplayMode.FitToScreen),
                    ImageSmoothing = context.GetValue(levelEvent, "imageSmoothing", true),
                    LockRotation = context.GetValue(levelEvent, "lockRot", false),
                    Loop = context.GetValue(levelEvent, "loopBG", true),
                    ScalingRatio = context.GetValue(levelEvent, "scalingRatio", 100f) / 100f
                });
            }

            PreviewStableSort.Sort(keyframes, (left, right) => left.Start.CompareTo(right.Start));
            Main.Logger.Log("[StaticPreview/CustomBackground] analyzed " + keyframes.Count + " events.");
        }

        public void Evaluate(PreviewEvaluationFrame frame)
        {
            int index = PreviewKeyframeSearch.FindLastAtOrBefore(keyframes, frame.Time);
            if (index == lastAppliedIndex)
            {
                return;
            }

            if (Apply(index < 0 ? defaultState : keyframes[index]))
            {
                lastAppliedIndex = index;
            }
        }

        public void EndSession()
        {
            keyframes.Clear();
            defaultState = null;
            lastAppliedIndex = int.MinValue;
            // 「这张图读不出来」的结论不能带出会话 —— 换一张谱面、
            // 或者用户把图补进目录之后，必须能重新试一次。
            attemptedLoads.Clear();
        }

        private static bool Apply(BackgroundKeyframe state)
        {
            scnGame game = scnGame.instance;
            scrCamera camera = scrCamera.instance;
            scnGame customLevel = ADOBase.customLevel;
            if (state == null || game == null || camera == null || customLevel == null
                || customLevel.custBG == null || customLevel.imgHolder == null)
            {
                return false;
            }

            camera.Bgcamstatic.backgroundColor = state.Color;
            scrCustomBackgroundSprite customBackground = customLevel.custBG;
            if (string.IsNullOrEmpty(state.Image))
            {
                customBackground.SetCustomBG(null, Color.white, false, true, true, 1f, false, true);

                // **没有背景图时，原生的 SetBackground() 还会铺一层「编辑器默认背景」**：
                // 瓷片开关与颜色、形状类型与颜色。只写 editorBG 的 SetActive
                // 会让这三样永远停在谱面刚载入时的值 —— 在设置页里改
                // defaultBGTileColor / defaultBGShapeType 之类的属性就看不到任何变化。
                ApplyDefaultBackground(game);
                return true;
            }

            // **图片可能还没被加载进 customSprites。**
            //
            // 原生的 SetBackground() 是 `custBG.SetBaseSprite(filePath, filename)`
            // —— 那一步会按谱面目录从磁盘读图并塞进 imgHolder。
            // 这里原来只做 TryGetValue，查不到就直接 return false 放弃这一帧；
            // 而 return false 还意味着 lastAppliedIndex 不前进，
            // 于是每一帧都重新尝试、永远查不到。
            //
            // 表现就是：在背景设置里换一张图，存档里改了、画面纹丝不动。
            TextureManager.CustomSprite sprite;
            if (!customLevel.imgHolder.customSprites.TryGetValue(state.Image, out sprite) || sprite == null)
            {
                // **同一个文件名只试着读一次盘。**
                //
                // 读不出来（文件不在谱面目录、格式不支持）时 Apply 会返回 false，
                // lastAppliedIndex 不前进 → 下一帧又进来。不加这道闸就是每帧
                // 一次 File.Exists + 一次贴图解码尝试，白白卡住主线程。
                // 换了图（文件名变了）自然会重新试。
                if (attemptedLoads.Add(state.Image))
                {
                    LoadBackgroundSprite(customLevel, state.Image);
                }
                if (!customLevel.imgHolder.customSprites.TryGetValue(state.Image, out sprite) || sprite == null)
                {
                    return false;
                }
            }

            customBackground.SetCustomBG(sprite, state.ImageColor,
                state.DisplayMode == BgDisplayMode.Tiled,
                state.Loop,
                state.DisplayMode == BgDisplayMode.FitToScreen,
                state.ScalingRatio,
                state.LockRotation,
                state.ImageSmoothing);
            customBackground.parallax.multiplier_x = state.Parallax.x;
            customBackground.parallax.multiplier_y = state.Parallax.y;
            if (game.editorBG != null)
            {
                game.editorBG.SetActive(false);
            }
            return true;
        }

        /// <summary>
        /// 按谱面目录从磁盘读一张背景图，塞进 <c>imgHolder.customSprites</c>。
        ///
        /// 原生 `SetBackground()` 走的是 `custBG.SetBaseSprite(filePath, filename)`，
        /// 它内部就是这一步。预览不能只靠“缓存里已经有”—— 在设置页
        /// 刚选一张新图时，缓存里根本没有它。
        /// </summary>
        private static void LoadBackgroundSprite(scnGame game, string filename)
        {
            if (game == null || game.imgHolder == null
                || string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(game.levelPath))
            {
                return;
            }

            try
            {
                string directory = System.IO.Path.GetDirectoryName(game.levelPath);
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }
                string filePath = System.IO.Path.Combine(directory, filename);
                if (!System.IO.File.Exists(filePath))
                {
                    return;
                }

                LoadResult status;
                game.imgHolder.GetOrAddSprite(filename, filePath, out status);
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview/CustomBackground] 读取背景图 " + filename
                    + " 失败：" + exception.Message);
            }
        }

        /// <summary>
        /// 没有背景图时，照原生 `SetBackground()` 把“编辑器默认背景”铺好。
        ///
        /// 原生那一段做四件事：决定 editorBG 显不显示、瓷片开不开与什么颜色、
        /// 形状类型、形状颜色。原来这里只做了第一件，
        /// 所以在设置页改 `defaultBGTileColor` / `defaultBGShapeType` /
        /// `defaultBGShapeColor` 这三样时，画面一点反应都没有。
        ///
        /// 自定义 prefab（`customBGPrefab`）不在这里重建 —— 那要销毁并重新
        /// Instantiate 一整个 GameObject，代价很大，而它只在极少数内置谱上用。
        /// 已经在用自定义 prefab 的场景保持原样。
        /// </summary>
        private static void ApplyDefaultBackground(scnGame game)
        {
            LevelData levelData = game.levelData;
            if (levelData == null)
            {
                return;
            }

            bool visible = !string.IsNullOrEmpty(levelData.bgVideo)
                ? false
                : levelData.bgShowDefaultBGIfNoImage;
            if (game.editorBG != null)
            {
                game.editorBG.SetActive(visible);
            }

            // 用了自定义背景 prefab 时不碰那套瓷片/形状 —— 它们属于 prefab 自己，
            // 原生在同一条件下也是整段跳过的。
            TutorialBackground tutorial = game.tutorialBackground;
            if (tutorial == null || game.customEditorBG != null)
            {
                return;
            }

            try
            {
                if (tutorial.tile != null)
                {
                    tutorial.tile.gameObject.SetActive(levelData.bgShowDefaultBGTile);
                }
                tutorial.SetTileColor(levelData.bgDefaultBGTileColor);
                tutorial.SetShapeType(levelData.bgShapeType);
                if (levelData.bgShapeType == BGShapeType.SingleColor)
                {
                    tutorial.SetShapeColor(levelData.bgDefaultBGShapeColor);
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Error("[StaticPreview/CustomBackground] 铺默认背景失败：" + exception.Message);
            }
        }

        private static BackgroundKeyframe CreateDefault(LevelData levelData)
        {
            if (levelData == null)
            {
                return new BackgroundKeyframe
                {
                    StartTime = float.NegativeInfinity,
                    Color = Color.black,
                    Image = string.Empty,
                    ImageColor = Color.white,
                    Parallax = Vector2.zero,
                    DisplayMode = BgDisplayMode.FitToScreen,
                    ImageSmoothing = true,
                    Loop = true,
                    ScalingRatio = 1f
                };
            }

            BgDisplayMode displayMode = levelData.backgroundSettings == null
                ? BgDisplayMode.FitToScreen
                : levelData.backgroundSettings.Get<BgDisplayMode>("bgDisplayMode", BgDisplayMode.FitToScreen);
            return new BackgroundKeyframe
            {
                StartTime = float.NegativeInfinity,
                Color = levelData.backgroundColor,
                Image = levelData.bgImage,
                ImageColor = levelData.bgImageColor,
                Parallax = levelData.bgParallax,
                DisplayMode = displayMode,
                ImageSmoothing = levelData.bgSmoothing,
                LockRotation = levelData.bgLockRot,
                Loop = levelData.bgLooping,
                ScalingRatio = levelData.scalingRatio
            };
        }

        private static Color ReadColor(PreviewEventSnapshot levelEvent, string key, Color fallback)
        {
            try
            {
                return levelEvent.GetColor(key, fallback);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
