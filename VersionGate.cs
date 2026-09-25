using System;
using System.IO;
using System.Collections.Generic;
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
    /// 流程：查清单 → 低于 min 就下载新版 zip → 覆盖 mod 目录 →
    /// **提示用户关闭并重新打开游戏**。
    ///
    /// **为什么最后一步不自动重启。** 试过五种重启方式，全都在真实场景
    /// 里失败：`Application.OpenURL("steam://...")` 会阻塞十几秒；
    /// `cmd /c timeout &amp; start` 里的 `timeout` 遇到重定向 stdin 直接报错
    /// 退出（延迟等于没有）；`steam://launch` 起的实例会卡在 shader warmup；
    /// 直接 `Process.Start` 则旧实例还没退完就起了新的。
    /// 而**由游戏进程派生的任何助手**在真实流程里都会起出一个卡住的实例
    /// （`winhttp` 装了但 UMM 不被调用），同样的脚本手工跑却一切正常——
    /// 这条差异我始终没能在本地复现，所以不再赌它。
    ///
    /// 安装本身是可靠的（每次都能把版本号换掉），不可靠的只是"重启"这一步。
    /// 于是把它去掉：装完写一条日志，让用户自己重开。强制更新的语义不变
    /// ——不更新照样不让用——只是最后一步交给用户。
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
            // 备用。**只用 cdn.jsdelivr.net，不要用 gcore。**
            //
            // gcore 曾长期返回一份几个月前的旧副本，purge 也清不掉——
            // 实测同一时刻 cdn / fastly / testingcf 三个节点都是新的，
            // 只有 gcore 是旧的。而旧的清单危害是**静默**的：里面
            // min 和 latest 都是老版本，于是「当前版本 >= min」成立，
            // 判定成「版本正常」，用户重启多少次都不会更新，还查不出
            // 原因。宁可换一个已知健康的节点。
            new ManifestSource
            {
                Url = "https://cdn.jsdelivr.net/gh/{REPO}@main/version.json",
                IsGitHubApi = false
            },
            new ManifestSource
            {
                Url = "https://fastly.jsdelivr.net/gh/{REPO}@main/version.json",
                IsGitHubApi = false
            },
            new ManifestSource
            {
                Url = "https://testingcf.jsdelivr.net/gh/{REPO}@main/version.json",
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

        /// <summary>
        /// 上一次**成功装成**的版本号。
        ///
        /// 这里原来是一个「本次启动是重启来的」布尔标志，装完置上、
        /// 下次启动无条件跳过整个检查。那样有个实际问题：**一次启动
        /// 只能升一级**——用户升到 0.9.5 之后，我们在服务器上发了
        /// 0.9.6，但那次启动已经把检查跳过了，必须再手动重启一次才看得到。
        ///
        /// 换成记录「装过哪个版本」，判断的是「清单要的这个版本我是不是
        /// 已经装过了」。防无限重启的效果一样（装完版本号没变说明这个包
        /// 有问题，不再重试），但每次启动都会正常检查。
        /// </summary>
        private const string LastInstalledKey = "ADOFAIEditorStudio_LastInstalled";

        // 后台线程跑完把结果放这儿，主线程的 Update 取走。
        private static volatile string pendingManifest;
        private static volatile byte[] pendingZip;
        private static volatile bool workDone;
        private static volatile bool needZipDownload;
        private static volatile string pendingZipUrl;
        // 这一轮要试的下载地址（主 + 备用）。
        private static List<string> pendingZipUrls = new List<string>();
        // 这次要装的版本号，下载完成后 HandleDownloadFinished 要用。
        private static volatile string pendingLatest;

        internal static void Check(ModEntry mod)
        {
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

            /// <summary>
            /// 同时问所有源，取**版本号最大**的那份清单。
            ///
            /// 这里原本是「问到第一个能用的就停」。那个做法有个要命的
            /// 漏洞：CDN 会缓存分支内容，于是某个节点可能拿一份**几个月前**
            /// 的清单回来，而旧清单里 min 是老版本，判定结果就是「版本正常」
            /// ——用户永远收不到更新，且日志里没有任何异常可循。
            /// 真实发生过：API 限流 403 之后回退到 CDN，CDN 给的是旧副本。
            ///
            /// 改成全问一遍取最新。清单是个几百字节的常量文件，多问几个源
            /// 的代价可以忽略，换来的是「单个节点缓存过期」不再能骗过我们。
            ///
            /// 一个源都没成功时仍然返回 null，交给上层「清单不可达，
            /// 跳过检查」——不能让源挂一天就全员卡死。
            /// </summary>
            private void FetchManifestThread()
            {
                string best = null;
                Version bestVersion = null;

                for (int s = 0; s < ManifestSources.Length; s++)
                {
                    ManifestSource source = ManifestSources[s];
                    string url = source.Url.Replace("{REPO}", Repo);
                    string json = FetchManifest(source, url);
                    if (string.IsNullOrEmpty(json))
                    {
                        continue;
                    }

                    Version version = ManifestVersion(json);
                    if (version == null)
                    {
                        continue;
                    }

                    if (bestVersion == null || version > bestVersion)
                    {
                        bestVersion = version;
                        best = json;
                    }
                }

                pendingManifest = best;
                workDone = true;
            }

            /// <summary>
            /// 一份清单里的版本号，用来在多个源之间挑最新的一份。
            ///
            /// 看的是 min 和 latest 里**大**的那个：旧清单的特征正是
            /// 两个数一起变小。解析不出来时返回 null，那一份就作废——
            /// 宁可少一个候选，也不能拿一份读不懂的清单去比版本。
            /// </summary>
            private static Version ManifestVersion(string json)
            {
                try
                {
                    JObject root = JObject.Parse(json);
                    Version min = Parse((string)root["min"]);
                    Version latest = Parse((string)root["latest"]);
                    return min > latest ? min : latest;
                }
                catch
                {
                    return null;
                }
            }

            private void DownloadThread()
            {
                for (int i = 0; i < pendingZipUrls.Count && pendingZip == null; i++)
                {
                    if (i > 0)
                    {
                        Main.Logger.Log("[Update] 换备用地址重试：" + pendingZipUrls[i]);
                    }
                    pendingZip = TryDownload(pendingZipUrls[i]);
                }
                workDone = true;
            }

            /// <summary>
            /// 由 jsDelivr 的地址推出 raw.githubusercontent.com 的等价地址。
            ///
            ///   gcore.jsdelivr.net/gh/OWNER/REPO@REF/path
            ///   → raw.githubusercontent.com/OWNER/REPO/REF/path
            /// </summary>
            private static string BuildRawMirror(string url)
            {
                const string marker = "jsdelivr.net/gh/";
                int at = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    return null;
                }
                string tail = url.Substring(at + marker.Length);
                int slash = tail.IndexOf('/');
                if (slash <= 0)
                {
                    return null;
                }
                string ownerRepo = tail.Substring(0, slash);
                string rest = tail.Substring(slash + 1);
                int atSign = rest.IndexOf('@');
                if (atSign < 0)
                {
                    return null;
                }
                int pathSlash = rest.IndexOf('/', atSign);
                if (pathSlash < 0)
                {
                    return null;
                }
                string reference = rest.Substring(atSign + 1, pathSlash - atSign - 1);
                string path = rest.Substring(pathSlash + 1);
                return "https://raw.githubusercontent.com/" + ownerRepo + "/"
                    + reference + "/" + path;
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

                // 这个版本已经装过了，装完却还是旧版 —— 说明那个包本身
                // 有问题（或者文件被占用没换掉）。再装一次只会无限重启，
                // 所以放行，让用户至少能进游戏。
                string lastInstalled = PlayerPrefs.GetString(LastInstalledKey, "");
                if (!string.IsNullOrEmpty(lastInstalled) && lastInstalled == latest)
                {
                    Main.Logger.Error("[Update] 已经装过 " + latest
                        + "，但当前仍是 " + current + "；不再重试，以免反复重启。");
                    Finish();
                    return;
                }

                // 清单里通常只给一条地址（jsDelivr）。那条挂了就整趟更新
                // 失败，而同一份包在 raw.githubusercontent.com 上一定有。
                // 两个都记下来，挨个试。
                pendingZipUrls = new List<string>();
                if (!string.IsNullOrEmpty(zipUrl))
                {
                    pendingZipUrls.Add(zipUrl);
                }
                string mirror = BuildRawMirror(zipUrl);
                if (!string.IsNullOrEmpty(mirror) && mirror != zipUrl)
                {
                    pendingZipUrls.Add(mirror);
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
                pendingLatest = latest;
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

                Main.Logger.Log("[Update] 已装好 " + pendingLatest
                    + "。请关闭并重新打开游戏以生效。");
                PlayerPrefs.SetString(LastInstalledKey, pendingLatest);
                PlayerPrefs.Save();
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
            private static bool tlsConfigured;

            /// <summary>
            /// 显式把 TLS 拉到 1.2/1.3。
            ///
            /// Mono 的默认值可能还停在 TLS 1.0，而 jsDelivr 这类 CDN
            /// 早就只收 1.2+。表现是握手被对端直接掐断：
            ///   SecureChannelFailure (Authentication failed because
            ///   the remote party has closed the transport stream.)
            /// —— 看起来像断网，其实是协议版本谈不拢。
            ///
            /// 只设一次；用 try/catch 包住是因为个别运行时不认某些枚举值。
            /// </summary>
            private static void EnsureTls()
            {
                if (tlsConfigured)
                {
                    return;
                }
                tlsConfigured = true;
                try
                {
                    // 有 1.3 就带上，没有就算了。
                    const SecurityProtocolType modern =
                        SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                    ServicePointManager.SecurityProtocol = modern;
                }
                catch (Exception)
                {
                    try
                    {
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    }
                    catch (Exception)
                    {
                        // 这个运行时两样都不认，只能用它自己的默认值。
                    }
                }
            }

            private static byte[] TryDownload(string url, bool gitHubApi = false)
            {
                EnsureTls();
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

            private static Version Parse(string text)
            {
                Version parsed;
                return Version.TryParse(text, out parsed) ? parsed : new Version(0, 0);
            }
        }
    }
}
