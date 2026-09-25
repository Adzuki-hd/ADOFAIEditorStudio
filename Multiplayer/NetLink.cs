using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace ADOFAIEditorStudio.Multiplayer
{
    /// <summary>
    /// **一条** TCP 连接：后台读写线程 + 递给主线程的收发队列。
    ///
    /// 它只管「这条连接上把消息可靠地搬来搬去」，完全不关心消息是什么、
    /// 谁该收、要不要转给第三个人——那些都是
    /// <see cref="MultiplayerSession"/> 的事。
    ///
    /// 分两个类是因为星形拓扑：主机要同时接好几个人，每个人一条连接；
    /// 监听那一半在 <see cref="NetListener"/> 里，这里只是「一个对端」。
    ///
    /// **线程模型**（照 VersionGate.Runner 那套，但连接是长命的，所以多一条队列）：
    ///
    ///   后台线程   只做阻塞 I/O：Read / Write。
    ///              收到的消息放进 <see cref="incoming"/>，要发的从 <see cref="outgoing"/> 取。
    ///   主线程     每帧调 <see cref="DrainIncoming"/> 把消息捞出来处理，
    ///              要发的时候调 <see cref="Send"/> 丢进队列。
    ///
    /// 两边唯一的接触面就是那两个 <see cref="ConcurrentQueue{T}"/>，
    /// 所以不需要锁，也不可能互相阻塞游戏帧。
    ///
    /// **为什么走 TCP 不走 UDP**：做谱要的是「一条参数改动绝对不能丢」，
    /// 而不是低延迟。几百毫秒的延迟在做谱里毫无影响，但丢一条改动
    /// 会让人怀疑人生；而且 TCP 自带重传与保序，省掉自己实现的活。
    /// </summary>
    internal sealed class NetLink : IDisposable
    {
        /// <summary>消息头：4 字节长度（小端）+ UTF-8 的 JSON 载荷。</summary>
        internal const int HeaderBytes = 4;

        /// <summary>
        /// 单条消息的上限。谱面全文是里面最大的东西，几 MB 顶天了；
        /// 给到 32MB 是留足余量，同时挡住「对端发了个荒唐长度把我们撑爆」。
        /// </summary>
        private const int MaxMessageBytes = 32 * 1024 * 1024;

        private sealed class Envelope
        {
            // 字段名短，因为每条消息都要带上它们。
            public string t;
            public string d;
        }

        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> outgoing = new ConcurrentQueue<string>();

        /// <summary>
        /// 写串行化。
        ///
        /// 平时只有后台写线程碰 stream。但 <see cref="SendNow"/> 是主线程
        /// 直接写的（告别消息等不到队列），两条线程可能同时写同一条流——
        /// 一次「4 字节头 + 载荷」写到一半被插进另一条消息的头，接收端
        /// 就会按错误的长度去切帧，后面全乱。
        /// </summary>
        private readonly object writeLock = new object();

        private TcpClient client;
        private NetworkStream stream;
        private Thread reader;
        private Thread writer;
        private volatile bool running;
        private volatile bool connected;

        /// <summary>这条连接的对端标识。由上层在握手后填（名字之类）。</summary>
        internal string PeerLabel;

        /// <summary>最近一次出错的原因。给 UI 显示用。</summary>
        internal string LastError { get; private set; }

        internal bool IsConnected
        {
            get { return connected; }
        }

        // ---------------------------------------------------------------- 建立

        /// <summary>作为成员连到主机。</summary>
        internal static NetLink Connect(string host, int port, out string error)
        {
            error = null;
            var link = new NetLink();
            try
            {
                var tcp = new TcpClient();
                tcp.Connect(host, port);
                link.Attach(tcp);
                return link;
            }
            catch (Exception exception)
            {
                error = "连接 " + host + ":" + port + " 失败：" + exception.Message;
                link.Dispose();
                return null;
            }
        }

        /// <summary>主机侧：把接受下来的一条连接包成 NetLink。</summary>
        internal static NetLink FromAccepted(TcpClient accepted)
        {
            var link = new NetLink();
            link.Attach(accepted);
            return link;
        }

        private void Attach(TcpClient tcp)
        {
            // 关掉 Nagle：我们的消息都是小而零散的，攒包只会让它们更慢到。
            tcp.NoDelay = true;
            client = tcp;
            stream = tcp.GetStream();
            connected = true;
            running = true;

            reader = new Thread(ReadLoop) { IsBackground = true, Name = "AES-MP-Read" };
            reader.Start();
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "AES-MP-Write" };
            writer.Start();
        }

        // ---------------------------------------------------------------- 读

        private void ReadLoop()
        {
            byte[] header = new byte[HeaderBytes];
            try
            {
                while (running)
                {
                    if (!ReadExact(header, HeaderBytes))
                    {
                        break;
                    }

                    int length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > MaxMessageBytes)
                    {
                        LastError = "收到一条长度不合理的消息（" + length + " 字节），连接已断开。";
                        break;
                    }

                    byte[] payload = new byte[length];
                    if (!ReadExact(payload, length))
                    {
                        break;
                    }

                    incoming.Enqueue(Encoding.UTF8.GetString(payload));
                }
            }
            catch (Exception exception)
            {
                if (running)
                {
                    LastError = "读取连接失败：" + exception.Message;
                }
            }
            finally
            {
                connected = false;
            }
        }

        /// <summary>把整整 count 个字节读满；对端正常关闭时返回 false。</summary>
        private bool ReadExact(byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    return false;
                }
                offset += read;
            }
            return true;
        }

        // ---------------------------------------------------------------- 写

        /// <summary>
        /// 排一条消息出去。主线程调，立刻返回——真正的写发生在后台线程。
        /// </summary>
        internal void Send(string type, object payload)
        {
            if (!connected)
            {
                return;
            }

            string message = BuildMessage(type, payload);
            if (message != null)
            {
                outgoing.Enqueue(message);
            }
        }

        /// <summary>
        /// 立刻写出去，不走后台写线程。**只给「写完就要断」的消息用。**
        ///
        /// 走队列的话有个必然的竞争：消息刚进队列，连接就被 Dispose 了，
        /// 后台写线程（20ms 轮询）根本没来得及取走——消息其实没发出去。
        /// 而这类消息恰恰是「告别」，发不出去对面就只能靠连接断了去猜，
        /// 那和没告别没区别。
        ///
        /// 主线程上直接写十几个字节的开销可以忽略，不会卡帧。
        /// </summary>
        internal void SendNow(string type, object payload)
        {
            if (!connected)
            {
                return;
            }

            string message = BuildMessage(type, payload);
            if (message == null)
            {
                return;
            }

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                byte[] header = BitConverter.GetBytes(bytes.Length);
                lock (writeLock)
                {
                    stream.Write(header, 0, HeaderBytes);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
            }
            catch (Exception exception)
            {
                LastError = "发送失败：" + exception.Message;
            }
        }

        private string BuildMessage(string type, object payload)
        {
            try
            {
                var envelope = new Envelope
                {
                    t = type,
                    d = payload == null ? null : JsonConvert.SerializeObject(payload)
                };
                return JsonConvert.SerializeObject(envelope);
            }
            catch (Exception exception)
            {
                LastError = "消息序列化失败：" + exception.Message;
                return null;
            }
        }

        private void WriteLoop()
        {
            try
            {
                // connected 一假就退出。
                //
                // 连接断了之后**不再有任何人清理这条 NetLink**（上面刻意
                // 不做「一断就拆」），而 running 要等 Stop()/Dispose 才会
                // 清掉。只看 running 的话，这个线程会以 20ms 的节奏空转
                // 到游戏退出——每断开一次就漏一个线程。
                // 而连接已经断了，这个写线程再也不可能写出任何东西，
                // 直接退出是它唯一正确的归宿。
                while (running && connected)
                {
                    string message;
                    if (!outgoing.TryDequeue(out message))
                    {
                        // 队列空就歇一下。网络写入是毫秒级的事，
                        // 20ms 的轮询完全够，而且这是后台线程，不占游戏帧。
                        Thread.Sleep(20);
                        continue;
                    }

                    byte[] payload = Encoding.UTF8.GetBytes(message);
                    byte[] header = BitConverter.GetBytes(payload.Length);
                    // 和 SendNow 抢同一条流，必须串起来。
                    lock (writeLock)
                    {
                        stream.Write(header, 0, HeaderBytes);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                }
            }
            catch (Exception exception)
            {
                if (running)
                {
                    LastError = "写入连接失败：" + exception.Message;
                }
                connected = false;
            }
        }

        // ---------------------------------------------------------------- 每帧

        /// <summary>主线程每帧调：把后台收到的消息捞出来。捞空了就返回 null。</summary>
        internal string DrainIncoming()
        {
            string message;
            return incoming.TryDequeue(out message) ? message : null;
        }

        // ---------------------------------------------------------------- 解析

        /// <summary>把一条原始消息拆成 (类型, 载荷 JSON)。拆不动返回 false。</summary>
        internal static bool TryParse(string raw, out string type, out string payloadJson)
        {
            type = null;
            payloadJson = null;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            try
            {
                var envelope = JsonConvert.DeserializeObject<Envelope>(raw);
                if (envelope == null || string.IsNullOrEmpty(envelope.t))
                {
                    return false;
                }
                type = envelope.t;
                payloadJson = envelope.d;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把载荷 JSON 反序列化成具体类型。失败返回 default。</summary>
        internal static T Payload<T>(string payloadJson)
        {
            if (string.IsNullOrEmpty(payloadJson))
            {
                return default(T);
            }
            try
            {
                return JsonConvert.DeserializeObject<T>(payloadJson);
            }
            catch
            {
                return default(T);
            }
        }

        // ---------------------------------------------------------------- 收尾

        internal void Stop()
        {
            running = false;
            connected = false;

            try
            {
                if (stream != null)
                {
                    stream.Close();
                }
            }
            catch
            {
                // 关的时候出错无所谓，下面还有一层兜底。
            }
            try
            {
                if (client != null)
                {
                    client.Close();
                }
            }
            catch
            {
            }

            stream = null;
            client = null;

            // 两个线程都是后台线程，连接一断会自己退出；
            // 这里不等它们，免得卡住主线程。
            reader = null;
            writer = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>
    /// 主机的监听端：在被选定的端口上收人。
    ///
    /// 星形拓扑下所有连接都终结在主机，所以监听和「一条连接」是两件事，
    /// 分开之后主机可以同时握着好几条 <see cref="NetLink"/>。
    ///
    /// 同样是「后台线程接人 → 塞进队列 → 主线程每帧来取」的模式，
    /// 主线程永远不会等在 Accept 上。
    /// </summary>
    internal sealed class NetListener : IDisposable
    {
        private readonly ConcurrentQueue<TcpClient> accepted = new ConcurrentQueue<TcpClient>();
        private TcpListener listener;
        private Thread worker;
        private volatile bool running;

        internal string LastError { get; private set; }

        /// <summary>实际监听的端口。传 0 进来时这里是系统挑的那个，UI 要显示它。</summary>
        internal int Port { get; private set; }

        /// <summary>
        /// 在指定端口上监听。
        ///
        /// 传 0 表示「让系统随便挑一个空闲端口」——那对单机自测方便，
        /// 但**主机开房不该用它**：端口一变，用户就得回内网穿透的
        /// 控制台改隧道配置，朋友也得换地址。开房请用一个固定端口。
        ///
        /// 固定端口有被占用的可能（上一个游戏进程还没退干净、别的程序
        /// 占了），所以给几个候选依次试。试到的那个会显示在界面上，
        /// 用户配穿透时照着填。
        /// </summary>
        internal bool Start(int port)
        {
            // 候选端口：0（让系统挑）只需要试一次；否则连着往上试几个。
            int[] candidates = port == 0
                ? new[] { 0 }
                : new[] { port, port + 1, port + 2, port + 3, port + 4, port + 5 };

            string lastError = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                Stop();
                LastError = null;

                try
                {
                    listener = new TcpListener(IPAddress.Any, candidates[i]);
                    listener.Start();
                    Port = ((IPEndPoint)listener.LocalEndpoint).Port;

                    running = true;
                    worker = new Thread(AcceptLoop) { IsBackground = true, Name = "AES-MP-Accept" };
                    worker.Start();
                    return true;
                }
                catch (Exception exception)
                {
                    lastError = exception.Message;
                }
            }

            LastError = "端口 " + port + " 起连续试了 " + candidates.Length
                + " 个都被占用：" + lastError;
            Stop();
            return false;
        }

        private void AcceptLoop()
        {
            try
            {
                while (running)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    if (!running)
                    {
                        client.Close();
                        break;
                    }
                    accepted.Enqueue(client);
                }
            }
            catch (Exception exception)
            {
                if (running)
                {
                    LastError = "监听出错：" + exception.Message;
                }
            }
        }

        /// <summary>主线程每帧调：取一个刚连进来的人。没有就返回 null。</summary>
        internal TcpClient TakeAccepted()
        {
            TcpClient client;
            return accepted.TryDequeue(out client) ? client : null;
        }

        internal void Stop()
        {
            running = false;
            try
            {
                if (listener != null)
                {
                    listener.Stop();
                }
            }
            catch
            {
            }
            listener = null;
            worker = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
