using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityModManagerNet;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 启动时的版本闸门与自动更新。
    ///
    /// 流程：查清单 → 低于 min 就下载新版 zip → 覆盖 mod 目录 → 重启游戏。
    /// 用户全程不用做任何事，重启后就是新版。
    ///
    /// 为什么能热替换正在运行的 dll：UMM 启动时先把 .dll 复制成
    /// .dll.&lt;PID&gt;.cache 再加载副本，所以磁盘上那个 .dll 全程没被锁。
    /// 覆盖它不影响当前进程，下次启动自然生效。
    ///
    /// 用 HttpWebRequest 而不是 UnityWebRequest：
    /// 后者在 Unity 6 里既不读系统代理、也没有设置代理的 API
    /// （proxy 属性被移除，改由 UNITY_PROXYSERVER 环境变量控制）。
    /// 对一个走本地代理的开发机和一个「用户不该做任何配置」的分发目标
    /// 来说，那都不成立。.NET 的 HttpWebRequest 自己读系统代理，
    /// 有代理就走、没有就直连，两边都不用配置。
    ///
    /// 代价是它同步阻塞，所以整趟跑在后台线程上，结果回到主线程处理。
    /// </summary>
    internal static class VersionGate
    {
        /// <summary>
        /// 一个清单源。GitHub API 的响应结构和 raw 不一样：
        /// 它把文件内容包在 json 的 content 字段里、base64 编码，
        /// 所以两种要分开解析。
        /// </summary>
        private sealed class ManifestSource
        {
            internal string Url;
            internal bool IsGitHubApi;
        }

        // 依次尝试，第一个成功就用它。
        //
        // GitHub API 排第一，因为 raw 和 jsDelivr 都会缓存分支内容，
        // 改了清单要等很久才看到——对「强制更新」来说那等于改不动。
        // 实测：改完 min 之后 raw 和 jsDelivr 仍返回旧值，
        // 只有 API 立刻是新值。
        //
        // 代价是未认证的 API 限制 60 次/小时/IP。每个用户独立计数，
        // 而我们只在启动时请求一次，正常使用碰不到。
        private static readonly ManifestSource[] ManifestSources =
        {
            new ManifestSource
            {
                Url = "https://api.github.com/repos/{REPO}/contents/version.json",
                IsGitHubApi = true
            },
            // 备用。内容可能是旧的，但至少不会失败。
            new ManifestSource
            {
                Url = "https://gcore.jsdelivr.net/gh/{REPO}@main/version.json",
                IsGitHubApi = false
            },
            new ManifestSource
            {
                Url = "https://raw.githubusercontent.com/{REPO}/main/version.json",
                IsGitHubApi = false
            }
        };

        // GitHub 用户名/仓库名。改这里就够，上面两个源都从它拼出来。
        private const string Repo = "Adzuki-hd/ADOFAIEditorStudio-manifest";

        private const int TimeoutMs = 15000;

        // 「这次重启是更新引起的」标记。存在 PlayerPrefs 里，
        // 新进程起来能读到。
        //
        // 没有它的话，一旦清单写错（比如 min 比实际发出去的包还高），
        // 每次启动都会下载、替换、重启 —— 无限循环，用户再也进不去。
        private const string RestartFlagKey = "ADOFAIEditorStudio_UpdatedRestart";

        // 后台线程跑完把结果放这儿，主线程的 Update 取走。
        private static volatile string pendingManifest;
        private static volatile byte[] pendingZip;
        private static volatile bool workDone;
        private static volatile bool needZipDownload;
        private static volatile string pendingZipUrl;

        internal static void Check(ModEntry mod)
        {
            // 上次就是被更新重启的。跳过检查，否则清单一旦写错
            // 会无限重启。
            if (PlayerPrefs.GetInt(RestartFlagKey, 0) == 1)
            {
                PlayerPrefs.DeleteKey(RestartFlagKey);
                PlayerPrefs.Save();
                Main.Logger.Log("[Update] 上一次启动是更新引起的，跳过本次检查。");
                return;
            }

            var host = new GameObject("ADOFAIEditorStudio.VersionGate");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Runner>().Begin(mod);
        }

        /// <summary>
        /// 负责把后台线程的进度搬回主线程。
        ///
        /// 分两步：先查清单，需要在下载时再起第二个线程。
        /// 线程里绝不碰 Unity 的东西（PlayerPrefs、Application 都不行），
        /// 那些全在 Update 里做。
        /// </summary>
        private sealed class Runner : MonoBehaviour
        {
            private ModEntry mod;
            private bool downloading;

            internal void Begin(ModEntry entry)
            {
                mod = entry;
                var thread = new Thread(FetchManifestThread) { IsBackground = true };
                thread.Start();
            }

            private void FetchManifestThread()
            {
                string json = null;
                for (int s = 0; s < ManifestSources.Length && string.IsNullOrEmpty(json); s++)
                {
                    ManifestSource source = ManifestSources[s];
                    string url = source.Url.Replace("{REPO}", Repo);
                    json = FetchManifest(source, url);
                }
                pendingManifest = json;
                workDone = true;
            }

            private void DownloadThread()
            {
                pendingZip = TryDownload(pendingZipUrl);
                workDone = true;
            }

            private void Update()
            {
                if (!workDone)
                {
                    return;
                }
                workDone = false;

                if (downloading)
                {
                    HandleDownloadFinished();
                    return;
                }

                if (string.IsNullOrEmpty(pendingManifest))
                {
                    // 查不到。放行——源挂一天就全员锁死的话，
                    // 用户没有任何自救手段，而他们只会认为 mod 坏了。
                    Main.Logger.Log("[Update] 清单不可达，跳过版本检查。");
                    Finish();
                    return;
                }

                JObject root;
                try
                {
                    root = JObject.Parse(pendingManifest);
                }
                catch (Exception exception)
                {
                    Main.Logger.Error("[Update] 清单格式错误: " + exception.Message);
                    Finish();
                    return;
                }

                string min = (string)root["min"] ?? "0.0.0";
                string latest = (string)root["latest"] ?? min;
                string zipUrl = (string)root["url"] ?? "";
                Version current = mod == null ? new Version(0, 0) : mod.Version;

                if (current >= Parse(min))
                {
                    Main.Logger.Log("[Update] 版本正常（" + current
                        + "，要求不低于 " + min + "）。");
                    Finish();
                    return;
                }

                if (string.IsNullOrEmpty(zipUrl))
                {
                    Main.Logger.Error("[Update] 需要更新到 " + latest + "，但清单没给下载地址。");
                    Finish();
                    return;
                }

                Main.Logger.Log("[Update] 当前 " + current + " 低于 " + min
                    + "，开始更新到 " + latest + "。");

                downloading = true;
                pendingZipUrl = zipUrl;
                var thread = new Thread(DownloadThread) { IsBackground = true };
                thread.Start();
            }

            private void HandleDownloadFinished()
            {
                if (pendingZip == null || pendingZip.Length == 0)
                {
                    Main.Logger.Error("[Update] 下载失败，保持当前版本。");
                    Finish();
                    return;
                }

                string error;
                if (!Install(pendingZip, mod, out error))
                {
                    Main.Logger.Error("[Update] 安装失败：" + error);
                    Finish();
                    return;
                }

                Main.Logger.Log("[Update] 已装好，重启游戏以生效。");
                PlayerPrefs.SetInt(RestartFlagKey, 1);
                PlayerPrefs.Save();
                RestartGame();
            }

            private void Finish()
            {
                Destroy(gameObject);
            }

            /// <summary>
            /// 取一份清单。GitHub API 返回的是包装过的 json，
            /// 要剥两层才拿到真正的清单文本。
            /// </summary>
            private static string FetchManifest(ManifestSource source, string url)
            {
                byte[] data = TryDownload(url, source.IsGitHubApi);
                if (data == null || data.Length == 0)
                {
                    return null;
                }

                // 清单是 UTF-8 的 json。用不带 BOM 的解析，
                // JObject.Parse 认不认 BOM 看版本，这里先剥掉。
                string text = new UTF8Encoding(false).GetString(data).TrimStart('﻿');

                if (!source.IsGitHubApi)
                {
                    return text;
                }

                // 外层长这样：
                //   { "name": "version.json", "content": "eyJtaW4i...", ... }
                // content 是 base64 的清单原文。它里面还有换行，解出来
                // 就是正常 json。
                try
                {
                    JObject wrapper = JObject.Parse(text);
                    string encoded = (string)wrapper["content"];
                    if (string.IsNullOrEmpty(encoded))
                    {
                        Main.Logger.Log("[Update] GitHub API 响应里没有 content 字段。");
                        return null;
                    }
                    string decoded = Encoding.UTF8.GetString(
                        Convert.FromBase64String(encoded.Replace("\n", "").Replace("\r", "")));
                    return decoded.TrimStart('﻿');
                }
                catch (Exception exception)
                {
                    Main.Logger.Log("[Update] 解析 GitHub API 响应失败：" + exception.Message);
                    return null;
                }
            }

            /// <summary>
            /// 同步下载。失败时把原因打进日志——静默返回 null 的话，
            /// 「连不上」和「清单内容不对」在日志里长得一样，
            /// 而这两种情况的修法完全不同。
            /// </summary>
            private static byte[] TryDownload(string url, bool gitHubApi = false)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Timeout = TimeoutMs;
                    request.ReadWriteTimeout = TimeoutMs;
                    request.UserAgent = "ADOFAIEditorStudio";
                    request.AllowAutoRedirect = true;
                    // 走系统代理。有就自动用，没有就直连——
                    // 这正是 UnityWebRequest 在 Unity 6 上做不到的那件事。
                    request.Proxy = WebRequest.DefaultWebProxy;
                    if (gitHubApi)
                    {
                        // GitHub API 不认默认 Accept 时会拒绝请求。
                        request.Accept = "application/vnd.github.v3+json";
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        return buffer.ToArray();
                    }
                }
                catch (WebException exception)
                {
                    string detail = exception.Status.ToString();
                    if (exception.Response is HttpWebResponse errorResponse)
                    {
                        detail += " HTTP " + (int)errorResponse.StatusCode;
                    }
                    Main.Logger.Log("[Update] 请求失败 " + url
                        + " → " + detail + " / " + exception.Message);
                    return null;
                }
                catch (Exception exception)
                {
                    Main.Logger.Log("[Update] 请求异常 " + url
                        + " → " + exception.Message);
                    return null;
                }
            }

            /// <summary>
            /// 先解压到临时目录，校验通过再逐个覆盖。
            ///
            /// 不直接往 mod 目录里解压：一个下到一半的 zip 或者
            /// 中途失败的解压，会把安装留在半损坏状态，而那时候
            /// 用户连工作台都打不开，没法自己救。临时目录里失败了
            /// 就整段丢弃，原目录一个字节没动。
            /// </summary>
            private static bool Install(byte[] payload, ModEntry mod, out string error)
            {
                error = null;
                string modPath = mod == null ? null : mod.Path;
                if (string.IsNullOrEmpty(modPath) || !Directory.Exists(modPath))
                {
                    error = "mod 目录不存在。";
                    return false;
                }

                string stamp = DateTime.Now.Ticks.ToString();
                string zipPath = Path.Combine(Path.GetTempPath(), "aes-" + stamp + ".zip");
                string unpackPath = Path.Combine(Path.GetTempPath(), "aes-" + stamp);

                try
                {
                    File.WriteAllBytes(zipPath, payload);
                    Directory.CreateDirectory(unpackPath);

                    try
                    {
                        ZipUtils.Unzip(zipPath, unpackPath);
                    }
                    catch (Exception exception)
                    {
                        error = "解压失败：" + exception.Message;
                        return false;
                    }

                    // UMM 的 zip 约定是「外层一个同名文件夹」。
                    // 没有就说明打包方式和 UMM 不一致，宁可不动。
                    string root = unpackPath;
                    string[] files = Directory.GetFiles(root);
                    if (files.Length == 0)
                    {
                        string[] dirs = Directory.GetDirectories(root);
                        if (dirs.Length != 1)
                        {
                            error = "zip 结构不符合 UMM 约定（缺少外层文件夹）。";
                            return false;
                        }
                        root = dirs[0];
                        files = Directory.GetFiles(root);
                    }
                    if (files.Length == 0)
                    {
                        error = "zip 里没有文件。";
                        return false;
                    }

                    // 到这一步才动 mod 目录。
                    foreach (string file in files)
                    {
                        string destination = Path.Combine(modPath, Path.GetFileName(file));
                        if (File.Exists(destination))
                        {
                            File.Delete(destination);
                        }
                        File.Move(file, destination);
                    }
                    return true;
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    return false;
                }
                finally
                {
                    TryDeleteFile(zipPath);
                    TryDeleteDirectory(unpackPath);
                }
            }

            private static void TryDeleteFile(string path)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // 临时文件删不掉不影响结果，系统会自己清。
                }
            }

            private static void TryDeleteDirectory(string path)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                }
                catch
                {
                    // 同上。
                }
            }

            /// <summary>
            /// 重启游戏。走 Steam 启动能让 Steam 认得出是同一个游戏，
            /// 直接起 exe 的话 Steam 可能把它当成第二个实例。
            /// </summary>
            private static void RestartGame()
            {
                try
                {
                    if (ADOFAI.SteamIntegration.SteamController.initialized)
                    {
                        Application.OpenURL("steam://launch/977950/Dialog");
                    }
                    else
                    {
                        System.Diagnostics.Process.Start(
                            Application.dataPath.Replace("_Data", ".exe"));
                    }
                }
                catch (Exception exception)
                {
                    Main.Logger.Error("[Update] 重启失败，请手动重开游戏：" + exception.Message);
                    return;
                }
                Application.Quit();
            }

            private static Version Parse(string text)
            {
                Version parsed;
                return Version.TryParse(text, out parsed) ? parsed : new Version(0, 0);
            }
        }
    }
}
