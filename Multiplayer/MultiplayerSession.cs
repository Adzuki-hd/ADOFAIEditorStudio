using System;
using System.Collections.Generic;
using System.Net.Sockets;
using ADOFAI;
using Newtonsoft.Json;
using UnityEngine;

namespace ADOFAIEditorStudio.Multiplayer
{
    /// <summary>联机里的一个成员。</summary>
    internal sealed class PeerInfo
    {
        public int Id;
        public string Name;
        /// <summary>主机是不是自己（用来在界面上把人标出来）。</summary>
        public bool IsHost;
    }

    internal enum MultiplayerRole
    {
        None,
        Host,
        Member
    }

    internal enum MultiplayerState
    {
        /// <summary>没在联机。</summary>
        Idle,
        /// <summary>主机已开始监听，还没有人连进来。</summary>
        Listening,
        /// <summary>成员正在连主机。</summary>
        Connecting,
        /// <summary>连接已建立、握手完成。</summary>
        Ready
    }

    /// <summary>
    /// 联机会话：把 <see cref="NetLink"/> 的字节流和 <see cref="ChartTransfer"/>
    /// 的谱面逻辑串起来，管角色、成员表和状态机。
    ///
    /// **线程**：所有网络 I/O 都在 <see cref="NetLink"/> 的后台线程里；
    /// 这个类整个跑在主线程，每帧由工作台调一次 <see cref="Tick"/>
    /// 把收到的消息取出来处理。
    ///
    /// **同步分三层**：
    ///   ① **改值**走增量（`edit` 消息）——几百字节，对面即时刷新；
    ///   ② **结构改动**（增删事件、拖装饰物）没法增量（事件没有稳定
    ///      下标），改成整份重推，0.5 秒合并窗口，**全自动**；
    ///   ③ **加入时**发一次谱面全文，然后成员按需索取自己缺的音乐/贴图。
    ///
    /// **两边各自改过结构时会分叉，而且没法自动合并。** 谁的谱面都不含
    /// 对方那一笔，不管往哪个方向覆盖都会丢一边的活。要正确合并得做
    /// 真正的差异合并，那不搭服务器做不到。所以「重新同步」是唯一的手动
    /// 兜底：拿房主那份覆盖回来。
    ///
    /// **没有锁，也没有冲突检测——两个人改同一个值就是「后到的覆盖先到的」。**
    ///
    /// 这是刻意的取舍。做合作谱时大家各管各的段落，两人同时改**同一个
    /// 事件的同一个键**本身就很少见；为这种少见情况让每个人每次编辑都
    /// 先走一次网络往返取锁，代价是**每一处输入都慢半拍**，收益却只是
    /// 挡掉一个不常发生的场景。
    ///
    /// 更关键的是：锁会让**别人**看着你的界面变红、改不动，而对方并不
    /// 知道你在改什么、还要改多久。做谱需要随时互相帮改，一块被别人
    /// 按住、自己连碰都不能碰，比偶尔被覆盖更难受。
    ///
    /// 覆盖是可恢复的（撤回、或者让对方重发一次），锁是硬拦。
    /// 选可恢复的那个。
    ///
    /// 做成静态类而不是 MonoBehaviour：会话的生命周期跟着工作台走，
    /// 不需要自己的 GameObject，也省掉一套 OnDestroy 的拆除逻辑。
    /// </summary>
    internal static class MultiplayerSession
    {
        // ---------------------------------------------------------------- 消息类型

        private const string MsgHello = "hello";
        private const string MsgWelcome = "welcome";
        private const string MsgChartRequest = "chartreq";
        private const string MsgChart = "chart";
        private const string MsgMemberJoin = "memberjoin";
        private const string MsgMemberLeave = "memberleave";
        private const string MsgPing = "ping";
        private const string MsgPong = "pong";
        private const string MsgEdit = "edit";
        private const string MsgBye = "bye";
        private const string MsgMediaWant = "mediawant";
        private const string MsgMedia = "media";

        // ---------------------------------------------------------------- 载荷

        private sealed class HelloPayload
        {
            public string name;
        }

        private sealed class WelcomePayload
        {
            public int id;
            public List<PeerInfo> members;
            /// <summary>主机当前的谱面全文，每次加入都带。</summary>
            public string chart;
        }

        private sealed class ChartPayload
        {
            public string chart;
            public string fingerprint;
            /// <summary>推这份谱面的人的名字。用来在提示里说清是谁推的。</summary>
            public string from;
            /// <summary>
            /// 这是一次**自动重同步**：对方的结构改动推过来的，或者是你
            /// 点了「重新同步」拉的。接收端**直接落地，不弹任何确认**。
            ///
            /// 不能拿「对方是不是在会话副本里」当判据——房主不在副本里，
            /// 但他加事件时推过来的同样是自动重同步，照样不该弹。
            ///
            /// 自动重同步必然跳过确认，理由：
            ///   - 房间里的两边谱面不同是**常态**（成员从进房起就在副本上
            ///     干活），「和你本地的不像同一张」必然成立，每次都问
            ///     等于每次都要点一下；
            ///   - 成员那份是会话副本，消耗品，没有「改了半小时没存」可言。
            /// </summary>
            public bool resync;
        }

        private sealed class MemberPayload
        {
            public PeerInfo member;
        }

        private sealed class LeavePayload
        {
            public int id;
        }

        /// <summary>
        /// 媒体协商：我要这些文件，你有就发我。
        ///
        /// 只有主机应答。成员之间不直连，媒体由主机中转——反过来说，
        /// **成员目录里的媒体传不到别人那里**，这是星形拓扑的直接结果。
        /// 做合作谱时媒体应该由房主统一持有，所以这个限制是合理的。
        /// </summary>
        private sealed class MediaWantPayload
        {
            /// <summary>我缺的文件名。主机有就发，没有就跳过。</summary>
            public string[] names;
        }

        /// <summary>一个媒体文件。`data` 是 base64。</summary>
        private sealed class MediaPayload
        {
            public string name;
            public string data;
        }

        /// <summary>
        /// 一批最多要多少个媒体文件。
        ///
        /// 动态装饰物这类谱面可能有几千张逐帧贴图；一次全要会变成一个
        /// 巨大的清单加几千条消息。分批取得小单元，收完一批再要下一批。
        /// </summary>
        private const int MediaBatchSize = 64;

        /// <summary>
        /// 正在下载媒体的那张谱面。
        ///
        /// 分批取的时候，一批收完要接着算「还缺哪些」——那必须拿同一份
        /// 谱面去算，不能读内存（加入期间没重载，内存里还是旧的）。
        /// </summary>
        private static string mediaChart;

        /// <summary>
        /// 主机明确说过「我这儿也没有」的文件。
        ///
        /// 不记的话会无限循环：这批要了、对方没有、差集里它还在、
        /// 下一批又去要。记下来就不再把它们算进缺口。
        /// </summary>
        private static readonly HashSet<string> mediaUnavailable =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>媒体收发进度，给界面显示「正在接收音乐/贴图 3/12」。</summary>
        internal static string MediaStatus { get; private set; }
        private static int mediaPending;
        private static int mediaTotal;
        /// <summary>已经收完的总数（跨批次），用来报全程进度。</summary>
        private static int mediaDoneTotal;
        /// <summary>算出来还缺的总数，用来报全程进度。</summary>
        private static int mediaMissingTotal;

        // ------------------------------------------------------------ 加载遮罩

        /// <summary>
        /// 加入房间期间显示加载遮罩（黑屏 + 一行字 + 进度条）。
        ///
        /// **它同时也解决了一个真问题**：加入过程要重载好几次谱面，
        /// 中间态是「装饰物少一半、文字装饰物不见了」——用户会以为坏了。
        /// 盖住之后，结束再整体重建一次工作台，那些没自动刷新上的东西
        /// 就都正常了。
        /// </summary>
        internal static bool Loading { get; private set; }
        internal static string LoadingText { get; private set; }
        /// <summary>0~1；小于 0 表示还不知道进度。</summary>
        internal static float LoadingProgress { get; private set; }

        /// <summary>
        /// 收尾要等多久。
        ///
        /// 收到谱面 / 媒体之后，编辑器那边其实是异步重载的
        /// （`OpenLevel` 是协程），立刻重建工作台的话，它读到的还是
        /// 重载到一半的状态。等一小会儿再收尾，重建出来就是成品。
        /// </summary>
        private const float LoadingSettleSeconds = 2.5f;

        /// <summary>
        /// 遮罩最长显示多久。
        ///
        /// 万一对方一直没把谱面发过来（房主那边出问题、连接半死），
        /// 不能让用户永远卡在黑屏上——超时就放行，让他能操作，
        /// 界面上的状态文字会继续说明情况。
        /// </summary>
        private const float LoadingTimeoutSeconds = 45f;

        private static float finishLoadingAt;
        private static float loadingDeadline;

        /// <summary>
        /// 媒体齐了（或本来就不需要），开始收尾。
        ///
        /// **整个加入过程只有这一次重载**：把会话副本干净地打开，覆盖掉
        /// 之前在中间态下积累的一切。然后用 <see cref="LoadingSettleSeconds"/>
        /// 等它落定，再关遮罩 + 重建工作台。
        /// </summary>
        private static void ScheduleFinishLoading()
        {
            if (finishLoadingAt > 0f)
            {
                // 已经排上了（可能有多个触发点：媒体收完、没媒体、
                // 收到对方的自动重推）。只认第一次，免得重复重载。
                return;
            }

            ChartTransfer.OpenSessionCopyInEditor();
            Main.Logger.Log("[联机] 内容已就绪，开始最后一次重载。");
            finishLoadingAt = UnityEngine.Time.unscaledTime + LoadingSettleSeconds;
        }

        private static void TickLoading()
        {
            if (!Loading)
            {
                return;
            }
            float now = UnityEngine.Time.unscaledTime;
            if (finishLoadingAt > 0f && now >= finishLoadingAt)
            {
                finishLoadingAt = 0f;
                FinishLoading();
                return;
            }
            if (loadingDeadline > 0f && now >= loadingDeadline)
            {
                loadingDeadline = 0f;
                Main.Logger.Log("[联机] 等待超时，先放开加载遮罩。");
                FinishLoading();
            }
        }

        private static void BeginLoading(string text)
        {
            Loading = true;
            LoadingText = text;
            LoadingProgress = -1f;
            finishLoadingAt = 0f;
            loadingDeadline = UnityEngine.Time.unscaledTime + LoadingTimeoutSeconds;
        }

        private static void SetLoading(string text, float progress)
        {
            Loading = true;
            LoadingText = text;
            LoadingProgress = progress;
        }

        /// <summary>
        /// 收尾：先把遮罩关上，**再**重建工作台。
        ///
        /// 顺序不能反——重建会销毁并重建整套 UI，遮罩如果还开着，
        /// 那一瞬间会闪一下。先关掉，用户直接看到的就是重建好的成品。
        ///
        /// 重建本身是为了让**没自动刷新上的东西**（最明显的是文字装饰物）
        /// 显示出来，这比去追每一个漏刷的地方可靠。
        /// </summary>
        private static void FinishLoading()
        {
            if (!Loading)
            {
                return;
            }

            Loading = false;
            LoadingText = null;
            LoadingProgress = -1f;
            finishLoadingAt = 0f;
            loadingDeadline = 0f;

            if (Main.Overlay != null)
            {
                Main.Overlay.RebuildWorkbench();
            }
        }

        // ---------------------------------------------------------------- 状态

        private static NetListener listener;
        private static NetLink hostLink;

        /// <summary>主机侧：每个成员一条连接。成员侧这个字典是空的。</summary>
        private static readonly Dictionary<int, NetLink> memberLinks = new Dictionary<int, NetLink>();
        private static readonly Dictionary<int, PeerInfo> members = new Dictionary<int, PeerInfo>();

        private static int nextMemberId = 1;
        private static int localId;
        private static string localName = "未命名";

        internal static MultiplayerRole Role { get; private set; }
        internal static MultiplayerState State { get; private set; }
        internal static string StatusText { get; private set; }
        internal static string LastError { get; private set; }

        /// <summary>主机监听的端口（主机侧）。</summary>
        internal static int ListenPort
        {
            get { return listener == null ? 0 : listener.Port; }
        }

        internal static bool IsActive
        {
            get { return State != MultiplayerState.Idle; }
        }

        internal static ICollection<PeerInfo> Members
        {
            get { return members.Values; }
        }

        // ---------------------------------------------------------------- 开房 / 加入

        /// <summary>当主机，开始等人。端口传 0 让系统随便挑（单机双开自测用）。</summary>
        internal static bool StartHosting(string playerName, int port)
        {
            // **开房必须有谱面。**
            //
            // 主机是内容的源头：成员进来是要拿他的谱面、他引用的音乐和
            // 贴图。空手开房的话，别人连上来只会拿到一份空谱面，
            // 而且媒体目录也定不下来（那么多文件从哪个目录找）。
            // 所以这里直接拒绝，并说清要做什么。
            if (!ChartTransfer.HasOpenLevel)
            {
                LastError = "请先打开一张谱面再开房 —— 加入的人要拿你的谱面和它用到的音乐、贴图。";
                return false;
            }

            Stop();
            localName = string.IsNullOrWhiteSpace(playerName) ? "主机" : playerName.Trim();

            listener = new NetListener();
            if (!listener.Start(port))
            {
                LastError = listener.LastError;
                listener = null;
                return false;
            }

            Role = MultiplayerRole.Host;
            State = MultiplayerState.Listening;
            localId = 0;
            nextMemberId = 1;

            members.Clear();
            members[localId] = new PeerInfo
            {
                Id = localId,
                Name = localName,
                IsHost = true
            };

            StatusText = "已开房，端口 " + listener.Port + "，等待加入";
            Main.Logger.Log("[联机] " + StatusText);
            return true;
        }

        /// <summary>当成员，连到主机。</summary>
        internal static bool JoinHost(string playerName, string host, int port)
        {
            Stop();
            localName = string.IsNullOrWhiteSpace(playerName) ? "成员" : playerName.Trim();

            State = MultiplayerState.Connecting;
            StatusText = "正在连接 " + host + ":" + port + "…";

            string error;
            hostLink = NetLink.Connect(host, port, out error);
            if (hostLink == null)
            {
                LastError = error;
                State = MultiplayerState.Idle;
                StatusText = null;
                Role = MultiplayerRole.None;
                return false;
            }

            Role = MultiplayerRole.Member;

            // 握手：带上名字、头像和本地谱面指纹。指纹一致的话主机
            // 只回成员表，不用把整份谱面发过来。
            hostLink.Send(MsgHello, new HelloPayload { name = localName });

            // **立刻切到会话副本，并把它在编辑器里打开。**
            //
            // 两件事必须一起做：只切写入目标而不重载，编辑器手里还是
            // 原文件那一份——用户按 Ctrl+S 时游戏写的是 `levelPath`，
            // 也就是原文件，正好绕过我们的重定向。
            //
            // 放在握手之前：副本建好之后所有后续写入（包括马上要收到的
            // 对方谱面）就都落在副本上了。
            //
            // 允许**空手加入**（没打开过谱面）：那时副本落在固定会话目录，
            // 而且编辑器场景可能还没起来，`OpenSessionCopyInEditor`
            // 会负责把场景载起来并自动打开它。
            if (ChartTransfer.BeginSessionCopy())
            {
                // **这里刻意不打开编辑器。** 加入期间只写盘，
                // 等媒体都到齐了再一次性干净重载（见 ScheduleFinishLoading）。
                // 中途每落一份就重载一次的话，中间态会一直变——而文字
                // 装饰物在重载到一半时最容易显示不出来。
            }
            else
            {
                LastError = "没能在旁边建出会话副本，接下来会直接编辑你打开的那份谱面，"
                    + "断开后改动会留在里面。";
                Main.Logger.Warning("[联机] " + LastError);
            }

            BeginLoading("下载谱面中…");
            StatusText = "正在握手…";
            return true;
        }

        /// <summary>断开并回到未联机状态。</summary>
        internal static void Stop()
        {
            // **主动离开要先打个招呼。**
            //
            // 没有这条消息，对面只能从「连接断了」去推断我们走了——
            // 而连接断了到底是人家主动退出、还是网线掉了，是分不出来的。
            // 分不出来就没法做判断：主动走了就该立刻清理（把他的锁
            // 还回去、从成员表里拿掉），网线掉了则应该等一会儿、
            // 别急着拆。
            //
            // 用 SendNow 而不是 Send：消息进队列后连接立刻就被 Dispose 了，
            // 后台写线程来不及取走，等于没发。
            if (Role == MultiplayerRole.Host)
            {
                foreach (NetLink link in memberLinks.Values)
                {
                    link.SendNow(MsgBye, null);
                }
            }
            else if (hostLink != null)
            {
                hostLink.SendNow(MsgBye, null);
            }

            if (listener != null)
            {
                listener.Dispose();
                listener = null;
            }
            if (hostLink != null)
            {
                hostLink.Dispose();
                hostLink = null;
            }
            foreach (NetLink link in memberLinks.Values)
            {
                link.Dispose();
            }
            memberLinks.Clear();
            members.Clear();

            Role = MultiplayerRole.None;
            State = MultiplayerState.Idle;
            StatusText = null;
            LastError = null;
            structuralDirty = false;
            nextAutoPush = 0f;
            mediaTotal = 0;
            mediaPending = 0;
            mediaDoneTotal = 0;
            mediaMissingTotal = 0;
            MediaStatus = null;
            mediaChart = null;
            mediaUnavailable.Clear();
            Loading = false;
            LoadingText = null;
            LoadingProgress = -1f;
            // 会话副本只属于这一次联机，断开就删掉。留着的话它会一直
            // 躺在谱面目录里，用户下次看到一堆 .aes-session.adofai 不知是什么。
            ChartTransfer.EndSessionCopy();
            // 排队中的谱面属于上一次会话，断线就不该再落地了。
            ChartTransfer.ResetApplyState();
            MultiplayerEditSink.Reset();
        }

        // ---------------------------------------------------------------- 每帧

        /// <summary>工作台每帧调一次。</summary>
        internal static void Tick()
        {
            if (State == MultiplayerState.Idle)
            {
                return;
            }

            // 重载窗口里排队的谱面。这个必须每帧推，跟有没有消息无关。
            ChartTransfer.TickPendingApply();
            TickAutoPush();
            TickLoading();

            // 主机：先收新进来的人。
            if (Role == MultiplayerRole.Host && listener != null)
            {
                if (!string.IsNullOrEmpty(listener.LastError) && string.IsNullOrEmpty(LastError))
                {
                    LastError = listener.LastError;
                }

                TcpClient accepted = listener.TakeAccepted();
                while (accepted != null)
                {
                    OnMemberConnected(accepted);
                    accepted = listener.TakeAccepted();
                }
            }

            // 成员：一条连接。
            //
            // **连接断了不动这个会话。**
            //
            // 这里原本是「一断就 Stop() 回到未联机」。那意味着任何一次
            // 网络抖动——穿透链路掉一下、对端几秒没响应——都会把整个
            // 房间关掉，用户得手动重开。而掉线大多只是暂时的，过一会儿
            // 连接自己就回来了（TCP 会重传、穿透隧道会重连）。
            //
            // 所以现在只**如实报告**，会话原样保留：对方回来了，
            // 数据照收照发；真回不来，用户看到提示自己点「断开」。
            // 把判断权交给人，比网络打一个嗝就把房间拆掉要好。
            if (hostLink != null)
            {
                DrainLink(hostLink, 0);
                if (!hostLink.IsConnected && string.IsNullOrEmpty(LastError))
                {
                    LastError = string.IsNullOrEmpty(hostLink.LastError)
                        ? "与主机的连接断了，正在等它恢复。"
                        : hostLink.LastError;
                    Main.Logger.Log("[联机] " + LastError);
                }
            }

            // 主机：每个人一条连接。
            //
            // 同上：连接断了只报告，不把成员从房间里踢掉。成员那边
            // 就算真退了，也只是他和主机之间这条连接没了——其他成员
            // 不受影响，他重连回来还能接着做。
            if (Role == MultiplayerRole.Host && memberLinks.Count > 0)
            {
                // 遍历期间不能动 memberLinks：DrainLink 会处理消息，而
                // 收到告别就会调 RemoveMember —— 那是往正在遍历的字典里删键，
                // 直接抛 InvalidOperationException。断开只报告、不清理，
                // 所以这里只需要一个「谁断了」的名单，遍历完再说。
                List<int> broken = null;
                foreach (KeyValuePair<int, NetLink> pair in memberLinks)
                {
                    DrainLink(pair.Value, pair.Key);
                    if (!pair.Value.IsConnected)
                    {
                        if (broken == null)
                        {
                            broken = new List<int>();
                        }
                        broken.Add(pair.Key);
                    }
                }

                if (broken != null && string.IsNullOrEmpty(LastError))
                {
                    var names = new List<string>();
                    for (int i = 0; i < broken.Count; i++)
                    {
                        NetLink link;
                        if (!memberLinks.TryGetValue(broken[i], out link))
                        {
                            continue;
                        }
                        string who = link.PeerLabel;
                        names.Add(string.IsNullOrEmpty(who) ? ("成员" + broken[i]) : who);
                    }

                    if (names.Count > 0)
                    {
                        LastError = "和「" + string.Join("、", names)
                            + "」的连接断了，正在等它恢复。";
                        Main.Logger.Log("[联机] " + LastError);
                    }
                }
            }

            if (State == MultiplayerState.Listening && memberLinks.Count > 0)
            {
                State = MultiplayerState.Ready;
                StatusText = "联机中（" + (memberLinks.Count + 1) + " 人）";
            }

        }

        private static void OnMemberConnected(TcpClient accepted)
        {
            NetLink link = NetLink.FromAccepted(accepted);
            int id = nextMemberId++;
            // 先把连接挂上，等它的 HELLO 到了再补名字。
            memberLinks[id] = link;
            members[id] = new PeerInfo { Id = id, Name = "加入中…" };
        }

        private static void DrainLink(NetLink link, int memberId)
        {
            string raw = link.DrainIncoming();
            int guard = 0;
            while (raw != null && guard++ < 64)
            {
                HandleMessage(link, memberId, raw);

                // 处理一条消息可能会把整个会话关掉（比如收到主机告别，
                // 或者主机自己收到「断开」）。这时 link 已经 Dispose 了，
                // 再去 DrainIncoming 就是碰一个拆掉的对象；而且继续
                // 处理后面的消息也没有意义——房间都没了。
                if (State == MultiplayerState.Idle)
                {
                    return;
                }

                raw = link.DrainIncoming();
            }
        }

        // ---------------------------------------------------------------- 消息处理

        private static void HandleMessage(NetLink link, int memberId, string raw)
        {
            string type;
            string json;
            if (!NetLink.TryParse(raw, out type, out json))
            {
                return;
            }

            switch (type)
            {
                case MsgBye:
                    // 对方主动告别。这是**确定的**离开，不是网络抖动，
                    // 所以立刻清理：还他的锁、从成员表里拿掉。
                    OnPeerSaidBye(link, memberId);
                    break;

                case MsgPing:
                    // 回一个。判活本身只要「收到过字节」就够，但回执能
                    // 让**对端也刷新时间戳**——两边都确认链路是双向通的，
                    // 而不是只有一边在自说自话。
                    link.Send(MsgPong, null);
                    break;

                case MsgPong:
                    // 收到什么不要紧，Pump 那边已经用「收到过字节」判活。
                    break;

                case MsgEdit:
                    // 对方改了一个值（参数、拖块、装饰物显隐锁定）。
                    // 主机要转发给别人，所以这个分支不能少——少了就是
                    // 「所有增量改动静默丢弃」，而且两边都看不出问题。
                    OnEditReceived(NetLink.Payload<EditPayload>(json));
                    break;

                case MsgMediaWant:
                    OnMediaWant(link, NetLink.Payload<MediaWantPayload>(json));
                    break;

                case MsgMedia:
                    OnMediaReceived(NetLink.Payload<MediaPayload>(json));
                    break;

                case MsgHello:
                    if (Role == MultiplayerRole.Host)
                    {
                        OnHello(link, memberId, NetLink.Payload<HelloPayload>(json));
                    }
                    break;

                case MsgWelcome:
                    if (Role == MultiplayerRole.Member)
                    {
                        OnWelcome(NetLink.Payload<WelcomePayload>(json));
                    }
                    break;

                case MsgChartRequest:
                    // 只有主机应答。把当前的谱面发给他。
                    if (Role == MultiplayerRole.Host)
                    {
                        SendChartTo(link, localName);
                    }
                    break;

                case MsgChart:
                    OnChartReceived(NetLink.Payload<ChartPayload>(json));
                    break;

                case MsgMemberJoin:
                    if (memberId == 0)
                    {
                        MemberPayload joined = NetLink.Payload<MemberPayload>(json);
                        if (joined != null && joined.member != null)
                        {
                            members[joined.member.Id] = joined.member;
                            RefreshStatus();
                        }
                    }
                    break;

                case MsgMemberLeave:
                    if (memberId == 0)
                    {
                        LeavePayload left = NetLink.Payload<LeavePayload>(json);
                        if (left != null)
                        {
                            members.Remove(left.id);
                            RefreshStatus();
                        }
                    }
                    break;
            }
        }

        private static void OnHello(NetLink link, int memberId, HelloPayload hello)
        {
            if (hello == null)
            {
                return;
            }

            var peer = new PeerInfo
            {
                Id = memberId,
                Name = string.IsNullOrWhiteSpace(hello.name) ? ("成员" + memberId) : hello.name,
                IsHost = false
            };
            // 先清掉这个人的**旧残影**。
            //
            // 断开不再自动清理，所以一条死连接会一直留在 memberLinks 和
            // members 里。他重连回来就是一个全新的 id，同一个名字挂两条 ——
            // 别人看到「某某」出现两次，而他占的锁也还挂在旧 id 上没人放。
            //
            // 靠名字认人：他没有稳定的用户 ID，而名字是唯一跨连接还能
            // 认出来的东西（同一个人的联机名不会变）。只在**旧连接确实
            // 已经断了**时清，避免把同名的另一个人误踢。
            List<int> ghosts = null;
            foreach (KeyValuePair<int, PeerInfo> pair in members)
            {
                if (pair.Key == memberId || pair.Key == localId)
                {
                    continue;
                }
                if (pair.Value == null
                    || !string.Equals(pair.Value.Name, peer.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                NetLink old;
                if (memberLinks.TryGetValue(pair.Key, out old) && old.IsConnected)
                {
                    // 另一个人，同名且还在线。不碰。
                    continue;
                }

                if (ghosts == null)
                {
                    ghosts = new List<int>();
                }
                ghosts.Add(pair.Key);
            }

            if (ghosts != null)
            {
                for (int i = 0; i < ghosts.Count; i++)
                {
                    Main.Logger.Log("[联机] 清掉「" + peer.Name
                        + "」的旧连接（#" + ghosts[i] + "）。");
                    RemoveMember(ghosts[i]);
                }
            }

            members[memberId] = peer;
            link.PeerLabel = peer.Name;
            RefreshStatus();

            // 告诉其他人来了个新成员。
            BroadcastExcept(memberId, MsgMemberJoin, new MemberPayload { member = peer });

            // 回执。**谱面全文一律带上，不做「指纹一致就免传」。**
            //
            // 免传那套是「两边谱面一样就不发」，而**指纹只算谱面文本**，
            // 算不到谱面目录里的音乐和贴图。于是最常见的情况恰好被跳过：
            // 两个人手里是同一张 main.adofai（指纹一致）→ 不传谱面 →
            // 成员那边也就不会去检查自己缺哪些媒体 → **音乐贴图永远传不过去**。
            //
            // 谱面才一两百 KB，媒体是几 MB；省这点没意义，换来的是
            // 一条能正常工作的媒体同步路径。
            link.Send(MsgWelcome, new WelcomePayload
            {
                id = memberId,
                members = new List<PeerInfo>(members.Values),
                chart = ChartTransfer.EncodeMemoryChart()
            });

            Main.Logger.Log("[联机] " + peer.Name + " 加入，已发谱面全文（媒体由对方按需来取）。");
        }

        private static void OnWelcome(WelcomePayload welcome)
        {
            if (welcome == null)
            {
                return;
            }

            localId = welcome.id;
            members.Clear();
            if (welcome.members != null)
            {
                for (int i = 0; i < welcome.members.Count; i++)
                {
                    PeerInfo peer = welcome.members[i];
                    if (peer != null)
                    {
                        members[peer.Id] = peer;
                    }
                }
            }

            State = MultiplayerState.Ready;
            RefreshStatus();
            Main.Logger.Log("[联机] 已加入，当前 " + members.Count + " 人。");

            if (!string.IsNullOrEmpty(welcome.chart))
            {
                // 用户刚点「加入」，意图很明确：就是要房主那份。
                // 这里再弹一次「和你本地的不像同一张」纯属添堵。
                OfferChart(welcome.chart, "主机", true);
            }
        }

        /// <summary>
        /// 收到一份谱面，落地它。
        ///
        /// **不做确认弹窗，也不再判断「像不像同一张谱」。** 两边谱面不同
        /// 是房间里的常态（成员从进房起就在会话副本上干活），问一次就是
        /// 每次都要点一下，自动同步也就不用谈了。
        ///
        /// 真正在保护用户的是另外两件事，而不是这个弹窗：
        ///   - 成员写的是**会话副本**，原文件全程不动；
        ///   - 房主写自己的原文件，但落盘前有带时间戳的 `.bak` 备份。
        ///
        /// `resync` 参数只用于日志区分，行为上不再有分支。
        /// </summary>
        private static void OfferChart(string chart, string from, bool resync)
        {
            if (string.IsNullOrEmpty(chart))
            {
                return;
            }

            // 指纹相同就没什么可落地的——本来就已经是同一份。
            //
            // **但媒体还是要查。** 指纹只算谱面文本，算不到谱面目录里的
            // 音乐和贴图；两边文本一样、其中一边没有那些文件，是完全正常
            // 的（也正是「同一个谱面文件、不同的人手里」的常态）。
            // 在这里直接返回，那些文件就永远传不过去。
            if (string.Equals(ChartTransfer.Fingerprint(chart), ChartTransfer.MemoryFingerprint(),
                    StringComparison.Ordinal))
            {
                StatusText = "谱面已是最新";
                RequestMissingMedia(chart);
                return;
            }

            ApplyChartNow(chart, from);
        }

        /// <summary>把一份谱面落到磁盘并让编辑器重载它。</summary>
        private static void ApplyChartNow(string chart, string from)
        {
            // 加入期间只写盘，不重载；重载由收尾那一步统一做。
            if (!ChartTransfer.ApplyChart(chart, !Loading))
            {
                LastError = "谱面落地失败，详见日志。";
                return;
            }

            StatusText = "已同步 " + (string.IsNullOrEmpty(from) ? "对方" : from) + " 的谱面";
            Main.Logger.Log("[联机] " + StatusText);

            // 谱面只是文本，它引用的**音乐和贴图是谱面目录里的文件**。
            // 对方那边没有这些文件的话，谱面就变成「没声音、没贴图」——
            // 所以落地之后立刻看自己缺哪些、缺就向主机要。
            RequestMissingMedia(chart);
        }

        // ---------------------------------------------------------------- 谱面收发

        /// <summary>
        /// <summary>
        /// 把当前谱面整份发给所有人。主机广播；成员发给主机，由它转发。
        ///
        /// **只有自动重同步会调它**——界面上的「推送」按钮已经去掉了，
        /// 改值走增量、结构改动自动整份推，两个方向都是自动的。
        ///
        /// 不往状态栏写「已推送」：结构改动每次都会推，每加一个事件就
        /// 闪一条提示是纯噪音。
        /// </summary>
        internal static void PushChart()
        {
            if (State != MultiplayerState.Ready && Role != MultiplayerRole.Host)
            {
                return;
            }

            // **必须真的开着谱面才能推送。**
            //
            // 这里原本只看「内存里能不能序列化出谱面」，而编辑器没打开任何
            // 关卡时 `levelData` 仍然是个空的默认谱面——于是会把一份空谱面
            // 推出去。接收端那道闸用的是 `CurrentLevelPath`，两边判据不一致，
            // 结果就是「一边说没关卡、一边说推送成功」，而主机的谱面
            // 可能被这份空内容覆盖掉。
            //
            // 用和接收端同一个判据，两边才会一致。
            string path = ChartTransfer.CurrentLevelPath;
            if (path == null)
            {
                return;
            }

            string chart = ChartTransfer.EncodeMemoryChart();
            if (string.IsNullOrEmpty(chart))
            {
                return;
            }

            var payload = new ChartPayload
            {
                chart = chart,
                fingerprint = ChartTransfer.Fingerprint(chart),
                from = localName,
                resync = true
            };

            if (Role == MultiplayerRole.Host)
            {
                BroadcastExcept(-1, MsgChart, payload);
            }
            else if (hostLink != null)
            {
                hostLink.Send(MsgChart, payload);
            }

            Main.Logger.Log("[联机] 已整份同步（" + localName + "）。");
        }

        /// <summary>从主机要一份最新的谱面。</summary>
        internal static void PullChart()
        {
            if (Role == MultiplayerRole.Member && hostLink != null)
            {
                hostLink.Send(MsgChartRequest, null);
                StatusText = "已请求最新谱面…";
            }
            else if (Role == MultiplayerRole.Host)
            {
                StatusText = "你是主机，谱面以你为准。";
            }
            else
            {
                LastError = "还没连上，无法拉取。";
            }
        }

        private static void SendChartTo(NetLink link, string requesterName)
        {
            string chart = ChartTransfer.EncodeMemoryChart();
            if (string.IsNullOrEmpty(chart))
            {
                return;
            }
            link.Send(MsgChart, new ChartPayload
            {
                chart = chart,
                fingerprint = ChartTransfer.Fingerprint(chart),
                from = localName,
                // 这是对「重新同步」的应答，对方是主动要这份的。
                resync = true
            });
        }

        private static void OnChartReceived(ChartPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.chart))
            {
                return;
            }

            // 主机收到成员的谱面时，要转给其他人——星形拓扑下
            // 成员之间没有直连，全靠主机转发。
            if (Role == MultiplayerRole.Host)
            {
                BroadcastExcept(-1, MsgChart, payload);
            }

            OfferChart(payload.chart, payload.from, payload.resync);
        }

        // ---------------------------------------------------------------- 本地改动上报

        /// <summary>
        /// 本地发生了一次写谱面的动作，广播给其他人。
        ///
        /// **只在真正联机时发。** 单机状态下这里每次都发就是纯开销，
        /// 而改值是最高频的动作。
        /// </summary>
        internal static void OnLocalEdit(EditPayload payload)
        {
            if (payload == null || !IsLinked)
            {
                return;
            }

            payload.from = localName;

            if (Role == MultiplayerRole.Host)
            {
                BroadcastExcept(-1, MsgEdit, payload);
                return;
            }

            if (hostLink == null)
            {
                return;
            }

            // **连接断着的时候要出声。**
            //
            // Send 在未连接时是静默返回的（见 NetLink.Send），于是断线期间
            // 本地改的东西一个都发不出去，而用户以为在正常联机——两个人
            // 各改各的，等发现时已经分叉很久了。这是最难查的一类问题，
            // 因为双方都没有任何异常可看。
            if (!hostLink.IsConnected)
            {
                LastError = "和主机的连接断了，你的改动暂时同步不出去。"
                    + "等连接恢复后请点「重新同步」把这一段补上。";
                return;
            }

            hostLink.Send(MsgEdit, payload);
        }

        /// <summary>
        /// 本地发生了结构性改动（增删事件、拖装饰物）。
        ///
        /// 这类**没法增量同步**：事件没有 ID，全靠数组下标定位，删掉一个
        /// 后面全体位移，按旧下标去发会改到别人的事件上。唯一正确的做法
        /// 是**整份重推**，让所有人重载到同一份。
        ///
        /// **不立刻推，而是排一个很短的延迟。** 事件会被追加到 `events` 的
        /// **末尾**，而时间轴展示时按时间排序——所以插入一个事件会让它
        /// 在时间轴上跳到正确位置，那不是新事件，但发出整份谱面是白费
        /// （一两百 KB）。延迟一下让同一阵操作的几次追加合成一次。
        ///
        /// 延迟取 0.5 秒：人手操作一次加事件/拖装饰物至少要几百毫秒，
        /// 所以正常使用下**每次操作都是一次推**，不会漏、也不会合并掉
        /// 什么；同时能挡住「脚本式连点」把带宽打满。
        /// </summary>
        internal static void OnLocalStructuralChange()
        {
            if (!IsLinked)
            {
                return;
            }

            structuralDirty = true;
            nextAutoPush = Mathf.Max(nextAutoPush, 0f);
            if (nextAutoPush <= 0f)
            {
                nextAutoPush = UnityEngine.Time.unscaledTime + AutoPushDelaySeconds;
            }
        }

        /// <summary>结构改动的合并窗口，见 <see cref="OnLocalStructuralChange"/>。</summary>
        private const float AutoPushDelaySeconds = 0.5f;

        private static bool structuralDirty;
        private static float nextAutoPush;

        /// <summary>工作台每帧调：到点就把结构改动整份推出去。</summary>
        private static void TickAutoPush()
        {
            if (!structuralDirty || nextAutoPush <= 0f)
            {
                return;
            }
            if (UnityEngine.Time.unscaledTime < nextAutoPush)
            {
                return;
            }

            structuralDirty = false;
            nextAutoPush = 0f;

            // 推的时候顺带把指纹记成「已落地」，这样自己那份绕一圈回来
            // 会被指纹那道闸挡掉，不会白白重载一次。
            string chart = ChartTransfer.EncodeMemoryChart();
            if (!string.IsNullOrEmpty(chart))
            {
                ChartTransfer.MarkApplied(chart);
            }

            PushChart();
            StatusText = "结构改动已自动同步";
        }

        /// <summary>真正联机中（连上了、不是单机开着面板）。</summary>        /// <summary>真正联机中（连上了、不是单机开着面板）。</summary>
        internal static bool IsLinked
        {
            get { return Role != MultiplayerRole.None && State != MultiplayerState.Idle; }
        }

        // ---------------------------------------------------------------- 增量改动

        /// <summary>
        /// 对端主动告别。
        ///
        /// 主机收到 → 那个成员退出了，清理他占的东西并通知其他人。
        /// 成员收到 → 主机关房了，本地会话跟着结束。
        ///
        /// **这条路径和「连接断了」是两回事。** 连接断了只报告、不清理
        /// （见 <see cref="Tick"/>），因为多半是网络抖一下、过会儿就回来；
        /// 而这条消息是对方明确说他走了，可以放心收摊。
        /// </summary>
        private static void OnPeerSaidBye(NetLink link, int memberId)
        {
            if (Role == MultiplayerRole.Host && memberId > 0)
            {
                RemoveMember(memberId);
                return;
            }

            // 成员收到主机告别：主机不干了，我们这边也收摊。
            LastError = "主机结束了联机。";
            Main.Logger.Log("[联机] " + LastError);
            Stop();
        }

        /// <summary>
        /// 收到别人的一次改值。
        ///
        /// 主机要转发——星形拓扑下成员之间没有直连，全靠主机中转。
        /// 转发**放在落地之前**：落地可能触发一次参数栏重建，先转出去，
        /// 别人的延迟就不受我这边的渲染开销影响。
        /// </summary>
        private static void OnEditReceived(EditPayload payload)
        {
            if (payload == null || payload.keys == null)
            {
                return;
            }

            if (Role == MultiplayerRole.Host)
            {
                BroadcastExcept(-1, MsgEdit, payload);
            }

            string who = string.IsNullOrEmpty(payload.from) ? "对方" : payload.from;

            if (MultiplayerEditSink.ApplyRemote(payload))
            {
                return;
            }

            // **落地失败一律不自动重同步，只提示。**
            //
            // 最直觉的反应是「下标对不上？那就整份重载一次」。但两边都
            // 各自增删过事件时，谁的谱面都不含对方那一笔，无论往哪个
            // 方向覆盖都会把一边的活弄丢——要正确合并得做真正的差异合并，
            // 那不搭服务器做不到。所以这里如实说清「不同步了」，
            // 并提示用「重新同步」拉一份房主的谱面统一回来。
            string detail = MultiplayerEditSink.LastApplyFailure
                == MultiplayerEditSink.ApplyFailure.StructureMismatch
                ? "两边的事件下标已经错位了"
                : "这次改动对不上本机的谱面";

            LastError = "「" + who + "」的改动没能同步过来（" + detail
                + "）。点「重新同步」可以拉一份房主的谱面，把大家统一到同一份。";
            StatusText = LastError;
            Main.Logger.Log("[联机] " + LastError);
        }

        // ---------------------------------------------------------------- 媒体传输

        /// <summary>
        /// 应用一份谱面之后，看看它引用的媒体我这边缺哪些，缺就向主机要。
        ///
        /// **只由成员发起。** 主机的媒体就是权威那份，没有「向谁要」可言；
        /// 而成员之间不直连，所以只能问主机。
        /// </summary>
        private static void RequestMissingMedia(string chart)
        {
            if (Role != MultiplayerRole.Member || hostLink == null)
            {
                return;
            }

            mediaChart = chart;
            mediaDoneTotal = 0;
            mediaMissingTotal = 0;
            RequestBatch();
        }

        /// <summary>
        /// 算一次「还缺哪些」，取一批要过来；一个都不缺就收尾。
        ///
        /// **为什么要分批**：动态装饰物这类谱面可能有几千张逐帧贴图，
        /// 一次全要会变成一个巨大的清单加几千条消息，中途断了全白费。
        /// 每批收完会再调一次这个函数，直到补齐为止。
        ///
        /// **差集每次都重算**（按磁盘上现在有什么），所以不需要记「上次
        /// 要过哪些」——收到一个就少一个，天然不会重复要。
        /// </summary>
        private static void RequestBatch()
        {
            if (hostLink == null)
            {
                ScheduleFinishLoading();
                return;
            }

            if (string.IsNullOrEmpty(ChartTransfer.MediaDirectory()))
            {
                // 没有谱面目录就没什么可取的。
                ScheduleFinishLoading();
                return;
            }

            // **引用从「收到的那份谱面」里抠，不读内存。**
            //
            // 加入期间刻意不重载编辑器（避免中间态反复闪），所以这一刻
            // 内存里还是旧谱面——从它抠出来的引用对应的是**上一张谱**。
            List<string> referenced = ChartTransfer.ExtractMediaNames(mediaChart);
            var missing = new List<string>();
            for (int i = 0; i < referenced.Count; i++)
            {
                if (!ChartTransfer.HasMedia(referenced[i])
                    && !mediaUnavailable.Contains(referenced[i]))
                {
                    missing.Add(referenced[i]);
                }
            }

            if (missing.Count == 0)
            {
                // 全都有了（或者这谱面根本不引用媒体），可以收尾。
                ScheduleFinishLoading();
                return;
            }

            int take = Mathf.Min(MediaBatchSize, missing.Count);
            var batch = missing.GetRange(0, take);

            // 全程进度：已收到的 + 本批还没回的 + 后面还缺的。
            mediaMissingTotal = missing.Count - batch.Count;
            mediaTotal = batch.Count;
            mediaPending = 0;
            int grandTotal = mediaDoneTotal + missing.Count;
            MediaStatus = "正在接收媒体 " + mediaDoneTotal + "/" + grandTotal;
            SetLoading("下载媒体中…", (float)mediaDoneTotal / Mathf.Max(1, grandTotal));

            Main.Logger.Log("[联机] 还缺 " + missing.Count + " 个媒体，本批取 " + take + " 个。");
            hostLink.Send(MsgMediaWant, new MediaWantPayload { names = batch.ToArray() });
        }

        /// <summary>
        /// 主机收到索取：有就发，没有就说明房主那边也没有。
        ///
        /// **没有的不报错。** 房主那边也缺的话（比如谱面引用的贴图他自己
        /// 都删了），这不是同步失败，只是这份谱面本来就不完整——报错只会
        /// 让用户以为联机坏了。
        /// </summary>
        private static void OnMediaWant(NetLink link, MediaWantPayload payload)
        {
            if (Role != MultiplayerRole.Host || payload == null
                || payload.names == null || link == null)
            {
                return;
            }

            int sent = 0;
            for (int i = 0; i < payload.names.Length; i++)
            {
                string name = payload.names[i];
                byte[] data = ChartTransfer.ReadMedia(name);

                // **没有的也要回一条**（数据留空）。
                //
                // 不回的话，成员那边会一直等这个文件、这批永远算不完；
                // 而且下一批又会把它要一遍，变成无限循环。回一条空的就是
                // 明确告诉对方「这个我这儿也没有，别再要了」。
                link.Send(MsgMedia, new MediaPayload
                {
                    name = name,
                    data = data == null ? null : Convert.ToBase64String(data)
                });

                if (data == null)
                {
                    Main.Logger.Log("[联机] 对方要「" + name + "」，本机也没有。");
                    continue;
                }
                sent++;
            }

            if (sent > 0)
            {
                Main.Logger.Log("[联机] 已发送 " + sent + " 个媒体文件。");
            }
        }

        /// <summary>收到一个媒体文件，落到谱面目录。</summary>
        private static void OnMediaReceived(MediaPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.name))
            {
                return;
            }

            byte[] data = null;
            try
            {
                data = Convert.FromBase64String(payload.data ?? string.Empty);
            }
            catch
            {
                Main.Logger.Warning("[联机] 媒体「" + payload.name + "」内容损坏，已丢弃。");
            }

            if (data != null)
            {
                ChartTransfer.WriteMedia(payload.name, data);
            }
            else
            {
                // 主机也没有。记下来，之后不再要它。
                mediaUnavailable.Add(payload.name);
            }

            mediaPending++;
            if (mediaPending < mediaTotal)
            {
                // **显示全程进度，不是本批进度。**
                //
                // 动态装饰物那种谱面有近千个文件、要分十几批；只报
                // 「12/64」用户会以为总共就 64 个。所以用「已收 +
                // 本批还没回 + 后面还缺的」估一个总盘子出来。
                int grandTotal = mediaDoneTotal + (mediaTotal - mediaPending) + mediaMissingTotal;
                SetLoading("下载媒体中…", grandTotal <= 0 ? -1f : (float)mediaDoneTotal / grandTotal);
                MediaStatus = "正在接收媒体 " + mediaDoneTotal + "/" + grandTotal;
                return;
            }

            // 这一批收完了。还有缺的就接着要下一批，直到补齐。
            mediaDoneTotal += mediaTotal;
            mediaTotal = 0;
            mediaPending = 0;
            RequestBatch();
        }

        // ---------------------------------------------------------------- 广播

        private static void BroadcastExcept(int exceptId, string type, object payload)
        {
            foreach (KeyValuePair<int, NetLink> pair in memberLinks)
            {
                if (pair.Key == exceptId)
                {
                    continue;
                }
                pair.Value.Send(type, payload);
            }
        }

        private static void RemoveMember(int id)
        {
            NetLink link;
            if (memberLinks.TryGetValue(id, out link))
            {
                string who = link.PeerLabel;
                link.Dispose();
                memberLinks.Remove(id);
                Main.Logger.Log("[联机] " + (string.IsNullOrEmpty(who) ? ("成员" + id) : who) + " 离开了。");
            }

            members.Remove(id);
            BroadcastExcept(-1, MsgMemberLeave, new LeavePayload { id = id });
            RefreshStatus();
        }

        private static void RefreshStatus()
        {
            if (State == MultiplayerState.Ready || Role == MultiplayerRole.Host)
            {
                int count = Role == MultiplayerRole.Host ? memberLinks.Count + 1 : members.Count;
                StatusText = count <= 1
                    ? "已开房，等待加入"
                    : "联机中（" + count + " 人）";
            }
        }
    }
}
