﻿using BililiveRecorder.Core.Config;
using NLog;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Timer = System.Timers.Timer;

namespace BililiveRecorder.Core
{
    /**
     * 直播状态监控
     * 分为弹幕连接和HTTP轮询两部分
     * 
     * 弹幕连接：
     * 一直保持连接，并把收到的弹幕保存到数据库
     * 
     * HTTP轮询：
     * 只有在监控启动时运行，根据直播状态触发事件
     * 
     * 
     * */
    public class StreamMonitor : IStreamMonitor
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        private const string defaulthosts = "broadcastlv.chat.bilibili.com";
        private const string CIDInfoUrl = "https://live.bilibili.com/api/player?id=cid:";

        private readonly Func<TcpClient> funcTcpClient;
        private readonly ConfigV1 config;

        private int dmChatPort = 2243;
        private string dmChatHost = defaulthosts;
#pragma warning disable IDE1006 // 命名样式
        private bool dmTcpConnected => dmClient?.Connected ?? false;
#pragma warning restore IDE1006 // 命名样式
        private Exception dmError = null;
        private TcpClient dmClient;
        private NetworkStream dmNetStream;
        private Thread dmReceiveMessageLoopThread;
        private CancellationTokenSource dmTokenSource = null;
        private readonly Timer httpTimer;

        public int Roomid { get; private set; } = 0;
        public bool IsMonitoring { get; private set; } = false;
        public event StreamStatusChangedEvent StreamStatusChanged;
        public event ReceivedDanmakuEvt ReceivedDanmaku;

        public StreamMonitor(int roomid, Func<TcpClient> funcTcpClient, ConfigV1 config)
        {
            this.funcTcpClient = funcTcpClient;
            this.config = config;

            Roomid = roomid;

            ReceivedDanmaku += Receiver_ReceivedDanmaku;

            dmTokenSource = new CancellationTokenSource();
            Repeat.Interval(TimeSpan.FromSeconds(30), () =>
            {
                if (dmNetStream != null && dmNetStream.CanWrite)
                {
                    try
                    {
                        SendSocketData(2);
                    }
                    catch (Exception) { }
                }
            }, dmTokenSource.Token);

            httpTimer = new Timer(config.TimingCheckInterval * 1000)
            {
                Enabled = false,
                AutoReset = true,
                SynchronizingObject = null,
                Site = null
            };
            httpTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    Check(TriggerType.HttpApi);
                }
                catch (Exception ex)
                {
                    logger.Log(Roomid, LogLevel.Warn, "获取直播间开播状态出错", ex);
                }
            };

            config.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName.Equals(nameof(config.TimingCheckInterval)))
                {
                    httpTimer.Interval = config.TimingCheckInterval * 1000;
                }
            };

            Task.Run(() => ConnectWithRetry());
        }

        private void Receiver_ReceivedDanmaku(object sender, ReceivedDanmakuArgs e)
        {
            switch (e.Danmaku.MsgType)
            {
                case MsgTypeEnum.LiveStart:
                    Task.Run(() => StreamStatusChanged?.Invoke(this, new StreamStatusChangedArgs() { type = TriggerType.Danmaku }));
                    break;
                case MsgTypeEnum.LiveEnd:
                    break;
                default:
                    break;
            }
        }

        #region 对外API

        public bool Start()
        {
            if (disposedValue)
            {
                throw new ObjectDisposedException(nameof(StreamMonitor));
            }

            IsMonitoring = true;
            httpTimer.Start();
            return true;
        }

        public void Stop()
        {
            if (disposedValue)
            {
                throw new ObjectDisposedException(nameof(StreamMonitor));
            }

            IsMonitoring = false;
            httpTimer.Stop();
        }

        public void Check(TriggerType type, int millisecondsDelay = 0)
        {
            if (disposedValue)
            {
                throw new ObjectDisposedException(nameof(StreamMonitor));
            }

            if (millisecondsDelay < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsDelay), "不能小于0");
            }

            Task.Run(() =>
            {
                Task.Delay(millisecondsDelay).Wait();
                if (BililiveAPI.GetRoomInfo(Roomid).isStreaming)
                {
                    StreamStatusChanged?.Invoke(this, new StreamStatusChangedArgs() { type = type });
                }
            });
        }

        #endregion
        #region 弹幕连接

        private void ConnectWithRetry()
        {
            bool connect_result = false;
            while (!dmTcpConnected && !dmTokenSource.Token.IsCancellationRequested)
            {
                Thread.Sleep((int)Math.Max(config.TimingDanmakuRetry, int.MaxValue));
                logger.Log(Roomid, LogLevel.Info, "连接弹幕服务器...");
                connect_result = Connect();
            }

            if (connect_result)
            {
                logger.Log(Roomid, LogLevel.Info, "弹幕服务器连接成功");
            }
        }

        private bool Connect()
        {
            if (dmTcpConnected) { return true; }

            try
            {
                FetchServerAddress(Roomid, ref dmChatHost, ref dmChatPort);

                dmClient = funcTcpClient();
                dmClient.Connect(dmChatHost, dmChatPort);
                dmNetStream = dmClient.GetStream();

                dmReceiveMessageLoopThread = new Thread(ReceiveMessageLoop)
                {
                    Name = "ReceiveMessageLoop " + Roomid,
                    IsBackground = true
                };
                dmReceiveMessageLoopThread.Start();

                SendSocketData(7, "{\"roomid\":" + Roomid + ",\"uid\":0}");
                SendSocketData(2);

                return true;
            }
            catch (Exception ex)
            {
                dmError = ex;
                logger.Log(Roomid, LogLevel.Error, "连接弹幕服务器错误", ex);

                return false;
            }
        }

        private void ReceiveMessageLoop()
        {
            logger.Trace("ReceiveMessageLoop Started! " + Roomid);
            try
            {
                var stableBuffer = new byte[dmClient.ReceiveBufferSize];
                while (dmTcpConnected)
                {
                    dmNetStream.ReadB(stableBuffer, 0, 4);
                    var packetlength = BitConverter.ToInt32(stableBuffer, 0);
                    packetlength = IPAddress.NetworkToHostOrder(packetlength);

                    if (packetlength < 16)
                    {
                        throw new NotSupportedException("协议失败: (L:" + packetlength + ")");
                    }

                    dmNetStream.ReadB(stableBuffer, 0, 2);//magic
                    dmNetStream.ReadB(stableBuffer, 0, 2);//protocol_version 
                    dmNetStream.ReadB(stableBuffer, 0, 4);
                    var typeId = BitConverter.ToInt32(stableBuffer, 0);
                    typeId = IPAddress.NetworkToHostOrder(typeId);

                    dmNetStream.ReadB(stableBuffer, 0, 4);//magic, params?
                    var playloadlength = packetlength - 16;
                    if (playloadlength == 0)
                    {
                        continue;//没有内容了
                    }

                    typeId = typeId - 1;//和反编译的代码对应 
                    var buffer = new byte[playloadlength];
                    dmNetStream.ReadB(buffer, 0, playloadlength);
                    switch (typeId)
                    {
                        case 0:
                        case 1:
                        case 2:
                            {
                                var viewer = BitConverter.ToUInt32(buffer.Take(4).Reverse().ToArray(), 0); //观众人数
                                break;
                            }
                        case 3:
                        case 4://playerCommand
                            {
                                var json = Encoding.UTF8.GetString(buffer, 0, playloadlength);
                                try
                                {
                                    ReceivedDanmaku?.Invoke(this, new ReceivedDanmakuArgs() { Danmaku = new DanmakuModel(json) });
                                }
                                catch (Exception ex)
                                {
                                    logger.Warn(ex);
                                }
                                break;
                            }
                        case 5://newScrollMessage
                        case 7:
                        case 16:
                        default:
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                dmError = ex;
                // logger.Error(ex);

                logger.Debug("Disconnected " + Roomid);
                dmClient?.Close();
                dmNetStream = null;
                if (!(dmTokenSource?.IsCancellationRequested ?? true))
                {
                    logger.Warn(ex, "弹幕连接被断开，将尝试重连");
                    ConnectWithRetry();
                }
            }
        }

        private void SendSocketData(int action, string body = "")
        {
            const int param = 1;
            const short magic = 16;
            const short ver = 1;

            var playload = Encoding.UTF8.GetBytes(body);
            var buffer = new byte[(playload.Length + 16)];

            using (var ms = new MemoryStream(buffer))
            {
                var b = BitConverter.GetBytes(buffer.Length).ToBE();
                ms.Write(b, 0, 4);
                b = BitConverter.GetBytes(magic).ToBE();
                ms.Write(b, 0, 2);
                b = BitConverter.GetBytes(ver).ToBE();
                ms.Write(b, 0, 2);
                b = BitConverter.GetBytes(action).ToBE();
                ms.Write(b, 0, 4);
                b = BitConverter.GetBytes(param).ToBE();
                ms.Write(b, 0, 4);
                if (playload.Length > 0)
                {
                    ms.Write(playload, 0, playload.Length);
                }
                dmNetStream.Write(buffer, 0, buffer.Length);
                dmNetStream.Flush();
            }
        }

        private static void FetchServerAddress(int roomid, ref string chatHost, ref int chatPort)
        {
            try
            {
                var request2 = WebRequest.Create(CIDInfoUrl + roomid);
                request2.Timeout = 2000;
                using (var stream = request2.GetResponse().GetResponseStream())
                using (var sr = new StreamReader(stream))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml("<root>" + sr.ReadToEnd() + "</root>");
                    chatHost = doc["root"]["dm_server"].InnerText;
                    chatPort = int.Parse(doc["root"]["dm_port"].InnerText);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse errorResponse = ex.Response as HttpWebResponse;
                if (errorResponse?.StatusCode == HttpStatusCode.NotFound)
                { // 直播间不存在（HTTP 404）
                    logger.Log(roomid, LogLevel.Warn, "该直播间疑似不存在", ex);
                }
                else
                { // B站服务器响应错误
                    logger.Log(roomid, LogLevel.Warn, "B站服务器响应弹幕服务器地址出错", ex);
                }
            }
            catch (Exception ex)
            { // 其他错误（XML解析错误？）
                logger.Log(roomid, LogLevel.Warn, "获取弹幕服务器地址时出现未知错误", ex);
            }
        }

        #endregion

        #region IDisposable Support
        private bool disposedValue = false; // 要检测冗余调用

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    dmTokenSource?.Cancel();
                    dmTokenSource?.Dispose();
                    httpTimer?.Dispose();
                    dmClient?.Close();
                }

                dmNetStream = null;
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // 请勿更改此代码。将清理代码放入以上 Dispose(bool disposing) 中。
            Dispose(true);
        }
        #endregion
    }
}
