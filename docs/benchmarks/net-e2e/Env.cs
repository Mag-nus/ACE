using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using ACE.Common;
using ACE.Common.Cryptography;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.Managers;
using ACE.Server.Network.Packets;

namespace NetE2E
{
    /// <summary>
    /// Zero-overhead access to PRIVATE members of the real NetworkSession (UnsafeAccessor = reflection without the cost).
    /// Every use of these is listed in the report as "set/read by reflection".
    /// </summary>
    public static class Acc
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "nextSend")] public static extern ref DateTime NextSend(NetworkSession s);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "nextAck")] public static extern ref DateTime NextAck(NetworkSession s);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "nextResync")] public static extern ref DateTime NextResync(NetworkSession s);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lastCachedPacketPruneTime")] public static extern ref DateTime LastPrune(NetworkSession s);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cachedPackets")] public static extern ref ConcurrentDictionary<uint, ServerPacket> Cached(NetworkSession s);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lastReceivedPacketSequence")] public static extern ref uint LastRecvSeq(NetworkSession s);
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lastReceivedFragmentSequence")] public static extern ref uint LastRecvFragSeq(NetworkSession s);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "AcknowledgeSequence")] public static extern void Ack(NetworkSession s, uint sequence);
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "FlagEcho")] public static extern void FlagEcho(NetworkSession s, float clientTime);
    }

    public struct Dgram
    {
        public uint Seq; public PacketHeaderFlags Flags; public int Size; public ushort Id, Time; public string Queues; public uint AckValue; public int Frags;
        public override string ToString() => $"seq={Seq} size={Size} flags=[{Flags}]" + (Frags > 0 ? $" fragQueues=[{Queues}]" : "") + ((Flags & PacketHeaderFlags.AckSequence) != 0 ? $" ack={AckValue}" : "");
    }

    public enum InKind { AckClear, AckEnc, Action64, AckAction64, Ping12 }

    /// <summary>A real loopback UDP client socket plus the real server-side Session created for it.</summary>
    public sealed class Client
    {
        public Socket Sock; public IPEndPoint Ep; public Session S; public NetworkSession Net;
        public byte[] ClientSeed, ServerSeed;
        public ISAAC C2SIsaac;
        public uint NextSeq = 2, NextFragSeq = 1;
        public ConcurrentDictionary<uint, ServerPacket> Cache;
        private readonly byte[] rx = new byte[2048];
        private readonly byte[] tmp = new byte[1024];

        public List<Dgram> Drain()
        {
            var list = new List<Dgram>();
            while (Sock.Available > 0)
            {
                int n = Sock.Receive(rx);
                list.Add(Parse(rx, n));
            }
            return list;
        }

        static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));

        public static Dgram Parse(byte[] b, int n)
        {
            var d = new Dgram { Size = n, Seq = U32(b, 0), Flags = (PacketHeaderFlags)U32(b, 4), Id = U16(b, 12), Time = U16(b, 14) };
            int pos = 20; var f = d.Flags;
            if ((f & PacketHeaderFlags.RequestRetransmit) != 0) { uint cnt = U32(b, pos); pos += 4 + 4 * (int)cnt; }
            if ((f & PacketHeaderFlags.RejectRetransmit) != 0) { uint cnt = U32(b, pos); pos += 4 + 4 * (int)cnt; }
            if ((f & PacketHeaderFlags.AckSequence) != 0) { d.AckValue = U32(b, pos); pos += 4; }
            if ((f & PacketHeaderFlags.ConnectRequest) != 0) pos += 32;
            if ((f & PacketHeaderFlags.TimeSync) != 0) pos += 8;
            if ((f & PacketHeaderFlags.EchoResponse) != 0) pos += 8;
            if ((f & PacketHeaderFlags.BlobFragments) != 0)
            {
                var sb = new StringBuilder();
                while (pos + 16 <= n)
                {
                    ushort size = U16(b, pos + 10), queue = U16(b, pos + 14);
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(((GameMessageGroup)queue).ToString());
                    d.Frags++;
                    if (size < 16) break;
                    pos += size;
                }
                d.Queues = sb.ToString();
            }
            return d;
        }

        /// <summary>
        /// Builds one client->server datagram with the SERVER's own ServerPacket serialiser (the wire format is symmetric),
        /// consuming this client's ISAAC stream exactly as a real client would. Returns the size written at dst[dstOffset..].
        /// </summary>
        public int BuildC2S(InKind kind, uint ackValue, byte[] dst, int dstOffset)
        {
            var p = new ServerPacket();
            bool enc = kind != InKind.AckClear;
            bool ack = kind is InKind.AckClear or InKind.AckEnc or InKind.AckAction64;
            bool frag = kind is InKind.Action64 or InKind.AckAction64 or InKind.Ping12;
            PacketHeaderFlags f = 0;
            if (enc) f |= PacketHeaderFlags.EncryptedChecksum;
            if (ack) f |= PacketHeaderFlags.AckSequence;
            if (frag) f |= PacketHeaderFlags.BlobFragments;
            p.Header.Flags = f;
            if (ack) { p.InitializeDataWriter(); p.DataWriter.Write(ackValue); }
            if (frag)
            {
                int len = kind == InKind.Ping12 ? 12 : 64;
                var payload = new byte[len];
                for (int i = 12; i < len; i++) payload[i] = (byte)(i * 7 + NextFragSeq);
                BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xF7B1);                                 // GameMessageOpcode.GameAction
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), NextFragSeq);                  // game action sequence
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), kind == InKind.Ping12 ? 0x01E9u : 0xF61Cu); // PingRequest / MoveToState
                var spf = new ServerPacketFragment(payload);
                spf.Header.Sequence = NextFragSeq++;
                spf.Header.Id = 0x80000000;
                spf.Header.Count = 1;
                spf.Header.Index = 0;
                spf.Header.Queue = 0x09;
                p.Fragments.Add(spf);
            }
            // a cleartext pure ACK repeats the last sequence (NetworkSession.cs:350-351); everything else advances it
            p.Header.Sequence = kind == InKind.AckClear ? NextSeq - 1 : NextSeq++;
            p.Header.Id = S.Network.ClientId;
            p.Header.Time = 0;
            p.Header.Iteration = 0x14;
            if (enc) p.IssacXor = C2SIsaac.Next();
            p.CreateReadyToSendPacket(tmp, out var size);
            Buffer.BlockCopy(tmp, 0, dst, dstOffset, size);
            return size;
        }

        /// <summary>Feeds one datagram through the REAL receive path exactly as ConnectionListener.OnDataReceive does (lines 119-124).</summary>
        public void Deliver(byte[] buf, int size)
        {
            var packet = new ClientPacket();
            if (packet.Unpack(buf, size))
                NetworkManager.ProcessPacket(Env.L0, packet, Ep);
            packet.ReleaseBuffer();
        }

        public uint CurSeq => Net.ConnectionData.PacketSequence.CurrentValue;
        public string Keys() { var k = Cache.Keys.OrderBy(x => x).ToArray(); return "{" + string.Join(",", k.Select(x => x + ":" + ShortFlags(Cache[x].Header.Flags))) + "}"; }
        public static string ShortFlags(PacketHeaderFlags f)
        {
            var sb = new StringBuilder();
            if ((f & PacketHeaderFlags.EncryptedChecksum) != 0) sb.Append('E');
            if ((f & PacketHeaderFlags.BlobFragments) != 0) sb.Append('F');
            if ((f & PacketHeaderFlags.AckSequence) != 0) sb.Append('A');
            if ((f & PacketHeaderFlags.TimeSync) != 0) sb.Append('T');
            if ((f & PacketHeaderFlags.EchoResponse) != 0) sb.Append('e');
            if ((f & PacketHeaderFlags.RequestRetransmit) != 0) sb.Append('N');
            if ((f & PacketHeaderFlags.Retransmission) != 0) sb.Append('R');
            return sb.ToString();
        }
    }

    public static class Env
    {
        public static ConnectionListener L0, L1;      // real listeners: C2S port and S2C port (+1)
        public static Session[] SessionMap;            // NetworkManager.sessionMap (private static readonly), read by reflection
        public static string LogMode;
        public static readonly DateTime Far = DateTime.UtcNow.AddHours(6);

        public static void Init(string logMode)
        {
            LogMode = logMode;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // Program.cs:61

            if (logMode == "bridge")
            {
                // Same wiring as Program.cs:110-115: log4net configured from xml, bridged into Microsoft.Extensions.Logging.
                // The config is the shipped log4net.config(.example) with the same levels (root ALL, Packets INFO),
                // file appenders redirected into ./logs and no console appender.
                var dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                var cfgPath = Path.Combine(dir, "log4net.e2e.config");
                File.WriteAllText(cfgPath, Log4NetConfig.Replace("@DIR@", dir.Replace('\\', '/')));
                var repo = log4net.LogManager.GetRepository(Assembly.GetEntryAssembly());
                log4net.Config.XmlConfigurator.Configure(repo, new FileInfo(cfgPath));
                ACE.Common.Logging.LogManager.Factory.AddProvider(new ACE.Server.Logging.Log4NetBridgeProvider(repo));
            }
            else if (logMode != "none") throw new Exception("log=bridge|none");

            int port = FindFreePortPair();
            var cfg = new MasterConfiguration();
            cfg.Server.Network.Host = "127.0.0.1";
            cfg.Server.Network.Port = (uint)port;
            cfg.Server.Network.MaximumAllowedSessions = 1000;
            cfg.Server.Threading.NetworkManagerParallelOptions.MaxDegreeOfParallelism = 1;
            ConfigManager.Initialize(cfg);

            SocketManager.Initialize();   // REAL: creates two ConnectionListeners, binds them and starts BeginReceiveFrom on both
            var listeners = (ConnectionListener[])typeof(SocketManager).GetField("listeners", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            L0 = listeners[0]; L1 = listeners[1];
            if (L0.Socket == null || L1.Socket == null || !L0.Socket.IsBound || !L1.Socket.IsBound) throw new Exception("listeners not bound");
            if (SocketManager.GetMatchedConnectionListener(L0) != L1) throw new Exception("listener pairing");

            SessionMap = (Session[])typeof(NetworkManager).GetField("sessionMap", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            if (SessionMap.Length != 1000) throw new Exception("sessionMap size " + SessionMap.Length);

            InboundMessageManager.Initialize(); // REAL handler tables (reflection scan of ACE.Server)

            Console.WriteLine($"[env] .NET {Environment.Version}, {Environment.ProcessorCount} cpus, ServerGC={System.Runtime.GCSettings.IsServerGC}, latency={System.Runtime.GCSettings.LatencyMode}, log={logMode}, listeners {L0.ListenerEndpoint} / {L1.ListenerEndpoint}, sessionMap[{SessionMap.Length}]");
            Console.WriteLine($"[env] packetLog.IsEnabled(Debug)={ACE.Common.Logging.LogManager.GetLogger("Packets").IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug)}  log.IsEnabled(Debug)={ACE.Common.Logging.LogManager.GetLogger(typeof(NetworkSession)).IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug)}");
        }

        static int FindFreePortPair()
        {
            var rnd = new Random();
            for (int attempt = 0; attempt < 200; attempt++)
            {
                int p = 20000 + rnd.Next(0, 10000) * 2;
                try
                {
                    using var a = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    using var b = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    a.Bind(new IPEndPoint(IPAddress.Loopback, p));
                    b.Bind(new IPEndPoint(IPAddress.Loopback, p + 1));
                    return p;
                }
                catch (SocketException) { }
            }
            throw new Exception("no free port pair");
        }

        /// <summary>
        /// Creates a real client UDP socket on loopback and a REAL Session for it through the public NetworkManager.FindOrCreateSession
        /// (real Session/NetworkSession constructors, placed into the real sessionMap), then performs the non-database lines of the
        /// real handshake with public members only, leaving the session in the state of an in-world client.
        /// </summary>
        public static Client NewClient(bool smallRcv = true)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (smallRcv) sock.ReceiveBufferSize = 2048; // undrained clients: the kernel drops once this fills
            sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var ep = (IPEndPoint)sock.LocalEndPoint;

            var s = NetworkManager.FindOrCreateSession(L0, new IPEndPoint(ep.Address, ep.Port));
            if (s == null) throw new Exception("sessionMap full");
            var cd = s.Network.ConnectionData;
            var c = new Client { Sock = sock, Ep = new IPEndPoint(ep.Address, ep.Port), S = s, Net = s.Network, ClientSeed = (byte[])cd.ClientSeed.Clone(), ServerSeed = (byte[])cd.ServerSeed.Clone() };
            c.C2SIsaac = new ISAAC(c.ClientSeed);

            // AuthenticationHandler.AccountSelectCallback (lines 119-128, 229-230) without the database lookups
            var connectRequest = new PacketOutboundConnectRequest(Timers.PortalYearTicks, cd.ConnectionCookie, s.Network.ClientId, cd.ServerSeed, cd.ClientSeed);
            cd.DiscardSeeds();
            s.Network.EnqueueSend(connectRequest);
            s.SetAccount((uint)(1000 + s.Network.ClientId), "acct" + s.Network.ClientId, AccessLevel.Player);
            s.State = SessionState.AuthConnectResponse;
            s.Network.Update(); // flushes the ConnectRequest (sequence 0) through the C2S listener socket, as the first tick would

            // NetworkManager.ProcessPacket lines 78-80 (ConnectResponse arriving on port+1)
            s.SetS2CEndpoint(new IPEndPoint(ep.Address, ep.Port));
            s.State = SessionState.AuthConnected;
            s.Network.sendResync = true;

            // entering the world
            s.InitSessionForWorldLogin();
            s.State = SessionState.WorldConnected;
            s.Network.TimeoutTick = DateTime.UtcNow.AddHours(6).Ticks; // public setter; real sessions get this refreshed by every inbound packet

            c.Cache = Acc.Cached(s.Network);
            return c;
        }

        public static Client[] NewClients(int n, bool smallRcv = true)
        {
            var r = new Client[n];
            for (int i = 0; i < n; i++) r[i] = NewClient(smallRcv);
            return r;
        }

        public static void Remove(Client c)
        {
            NetworkManager.RemoveSession(c.S);
            c.Sock.Close();
        }

        /// <summary>Quiesce the timers of an in-world session (private fields, by accessor) so nothing is due.</summary>
        public static void PushTimers(Client c)
        {
            Acc.NextAck(c.Net) = Far;
            Acc.NextResync(c.Net) = Far;
        }

        public static void ClearMap() { for (int i = 0; i < SessionMap.Length; i++) SessionMap[i] = null; }

        const string Log4NetConfig = @"<log4net>
  <appender name=""RollingFileAppender"" type=""log4net.Appender.RollingFileAppender"">
    <file value=""@DIR@/ACE_Log.txt"" />
    <appendToFile value=""false"" />
    <rollingStyle value=""Size"" />
    <maxSizeRollBackups value=""2"" />
    <maximumFileSize value=""20480KB"" />
    <staticLogFileName value=""true"" />
    <layout type=""log4net.Layout.PatternLayout"">
      <conversionPattern value=""%date [%thread] %-5level (%logger) %message%newline"" />
    </layout>
    <threshold value=""ALL"" />
  </appender>
  <appender name=""RollingFileAppender-WARN"" type=""log4net.Appender.RollingFileAppender"">
    <file value=""@DIR@/ACE_Log-WARN.txt"" />
    <appendToFile value=""false"" />
    <rollingStyle value=""Size"" />
    <maxSizeRollBackups value=""2"" />
    <maximumFileSize value=""20480KB"" />
    <staticLogFileName value=""true"" />
    <layout type=""log4net.Layout.PatternLayout"">
      <conversionPattern value=""%date [%thread] %-5level (%logger) %message%newline"" />
    </layout>
    <threshold value=""WARN"" />
  </appender>
  <logger name=""Packets"">
    <level value=""INFO"" />
  </logger>
  <appender name=""asyncForwarder"" type=""Log4Net.Async.AsyncForwardingAppender,Log4Net.Async"">
    <appender-ref ref=""RollingFileAppender"" />
    <appender-ref ref=""RollingFileAppender-WARN"" />
  </appender>
  <root>
    <level value=""ALL"" />
    <appender-ref ref=""asyncForwarder"" />
  </root>
</log4net>";
    }

    public sealed class Runs
    {
        public readonly List<double> Ns = new(), B = new();
        public void Add(double ns, double b) { Ns.Add(ns); B.Add(b); }
        public double Best => Ns.Min();
        public double Med => U.Median(Ns);
        public double Bytes => U.Median(B);
        public string Fmt(string unit = "ns") => $"{Best,9:F1} {unit} (med {Med,9:F1})  {Bytes,9:F1} B";
    }

    public static class U
    {
        public static double Ns(long ticks) => ticks * 1e9 / Stopwatch.Frequency;
        public static double Median(List<double> v) { var s = v.OrderBy(x => x).ToArray(); return s.Length == 0 ? double.NaN : (s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2); }
        public static void Warm(Action body, int rounds = 4) { for (int i = 0; i < rounds; i++) { body(); Thread.Sleep(300); } }
        public static void H(string s) { Console.WriteLine(); Console.WriteLine("=== " + s + " ==="); }
        /// <summary>Cost of one empty measurement bracket (2 x GetAllocatedBytesForCurrentThread + 2 x Stopwatch.GetTimestamp), ns. Included in every bracketed figure.</summary>
        public static double BracketNs()
        {
            double best = double.MaxValue;
            for (int r = 0; r < 6; r++)
            {
                long t = 0, b = 0; const int n = 2_000_000;
                for (int i = 0; i < n; i++)
                {
                    long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                    long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                    t += t1 - t0; b += a1 - a0;
                }
                // what a bracket adds to the accumulated time is the t0..t1 gap; the calls outside it are not counted
                best = Math.Min(best, Ns(t) / n);
                if (r < 2) Thread.Sleep(200);
            }
            return best;
        }
        [DllImport("libc", SetLastError = true)] static extern long syscall(long n);
        public static long GetTid() => syscall(186);
        public static double Arg(string[] a, string key, double def) { foreach (var x in a) if (x.StartsWith(key + "=")) return double.Parse(x.Substring(key.Length + 1), System.Globalization.CultureInfo.InvariantCulture); return def; }
        public static string ArgS(string[] a, string key, string def) { foreach (var x in a) if (x.StartsWith(key + "=")) return x.Substring(key.Length + 1); return def; }
    }
}
