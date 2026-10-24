using System;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 取游戏原生「文件参数」浏览按钮上那张文件夹图标。
    ///
    /// 原版 `PropertyControl_File` 的浏览按钮是一张图，不是文字；我们这边一直是
    /// 文字「浏览」。这里在运行时从**任意一份已存在的原生控件实例**上把 sprite
    /// 抠下来缓存起来，取不到就返回 null（调用方退回文字）。
    ///
    /// 用反射按类型名找，不直接引用那个类型 —— 它属于游戏程序集，
    /// 直接引用会让这个文件跟着游戏的类名绑死。
    /// </summary>
    internal static class NativeFileIcon
    {
        private static Sprite cached;
        private static bool tried;

        internal static Sprite Get()
        {
            if (tried)
            {
                return cached;
            }
            tried = true;

            try
            {
                MonoBehaviour[] all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour behaviour = all[i];
                    if (behaviour == null)
                    {
                        continue;
                    }
                    string typeName = behaviour.GetType().Name;
                    if (typeName.IndexOf("File", StringComparison.OrdinalIgnoreCase) < 0
                        || typeName.IndexOf("PropertyControl", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    Image[] images = behaviour.GetComponentsInChildren<Image>(true);
                    for (int j = 0; j < images.Length; j++)
                    {
                        Sprite sprite = images[j].sprite;
                        if (sprite == null)
                        {
                            continue;
                        }
                        string name = sprite.name.ToLowerInvariant();
                        if (name.Contains("folder") || name.Contains("file")
                            || name.Contains("open") || name.Contains("browse"))
                        {
                            cached = sprite;
                            return cached;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Main.Logger.Warning("[文件图标] 取原生图标失败：" + exception.Message);
            }

            return cached;
        }
    }
}
