using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;

using Microsoft.Extensions.Logging;

using ACE.Common.Cryptography;
using ACE.Server.Entity.Actions;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;

namespace Bench
{
    public static class Hash32Fast
    {
        // Same function as ACE.Common Hash32.Calculate: (len << 16) + sum of LE uint32 words + tail bytes big-endian-shifted.
        public static uint Calculate(ReadOnlySpan<byte> data)
        {
            int length = data.Length;
            uint checksum = (uint)length << 16;
            int words = length >> 2;
            var u = MemoryMarshal.Cast<byte, uint>(data.Slice(0, words * 4)); // little-endian host (x64/arm64)
            int i = 0;
            if (Vector128.IsHardwareAccelerated && u.Length >= 8)
            {
                var acc = Vector128<uint>.Zero;
                for (; i <= u.Length - 4; i += 4) acc += Vector128.Create<uint>(u.Slice(i, 4));
                checksum += Vector128.Sum(acc);
            }
            for (; i < u.Length; i++) checksum += u[i];
            int j = words * 4, shift = 3;
            while (j < length) checksum += (uint)(data[j++] << (8 * shift--));
            return checksum;
        }
    }

    sealed class DispatchInfo { public Action<ClientMessage, object> Handler; public int State; }

    // single-allocation replacement for the closure + Action + ActionEventDelegate in InboundMessageManager.HandleClientMessage
    sealed class InboundMessageAction : ActionEventBase
    {
        private readonly DispatchInfo info; private readonly ClientMessage message; private readonly object session;
        public InboundMessageAction(DispatchInfo i, ClientMessage m, object s) { info = i; message = m; session = s; }
        public override Tuple<IActor, IAction> Act() { info.Handler(message, session); return base.Act(); }
    }

    // count-bounded, sequence-indexed retransmit cache (alternative to ConcurrentDictionary + LINQ trim)
    sealed class RetransmitRing<T> where T : class
    {
        private readonly T[] ring; private readonly uint[] seqs; private readonly int mask; private uint low = 1, high; private readonly object gate = new();
        public RetransmitRing(int pow2) { ring = new T[pow2]; seqs = new uint[pow2]; mask = pow2 - 1; }
        public void Add(uint seq, T p)
        {
            lock (gate)
            {
                if (seq <= high && seqs[seq & mask] == seq && ring[seq & mask] != null) return; // TryAdd semantics
                if (seq > high) high = seq;
                while (high - low >= (uint)ring.Length) { ring[low & mask] = null; low++; } // count cap (perf-network P.8)
                ring[seq & mask] = p; seqs[seq & mask] = seq;
            }
        }
        public bool TryGet(uint seq, out T p)
        {
            lock (gate)
            {
                if (seq >= low && seq <= high && seqs[seq & mask] == seq) { p = ring[seq & mask]; return p != null; }
                p = null; return false;
            }
        }
        public void Ack(uint seq) // remove all < seq
        {
            lock (gate)
            {
                if (seq > high + 1) seq = high + 1;
                while (low < seq) { ring[low & mask] = null; low++; }
            }
        }
    }

    public static class MicroBench
    {
        static readonly DispatchInfo Info = new DispatchInfo { Handler = (m, s) => { }, State = 1 };
        static readonly ActionQueue Queue = new ActionQueue();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void HandleClientMessageLegacy(ClientMessage message, object session, Dictionary<uint, DispatchInfo> handlers)
        {
            var opcode = message.Opcode;
            if (handlers.TryGetValue(opcode, out var messageHandlerInfo))
            {
                if (messageHandlerInfo.State == 1)
                {
                    Queue.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        if (messageHandlerInfo.State == 2 && session == null) return;
                        try { messageHandlerInfo.Handler.Invoke(message, session); }
                        catch (Exception) { Console.WriteLine(opcode); }
                    }));
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void HandleClientMessageNew(ClientMessage message, object session, Dictionary<uint, DispatchInfo> handlers)
        {
            if (handlers.TryGetValue(message.Opcode, out var info) && info.State == 1)
                Queue.EnqueueAction(new InboundMessageAction(info, message, session));
        }

        sealed class Recv { public int Count; public void EnqueueSend(params GameMessage[] m) { Count += m.Length; } public void EnqueueSendSpan(params ReadOnlySpan<GameMessage> m) { Count += m.Length; } public void EnqueueSendOne(GameMessage m) { Count++; } }

        public static void Run()
        {
            H.Header("Hash32 (ACE.Common, netstandard2.0) vs vectorised; per call");
            foreach (int len in new[] { 16, 20, 68, 200, 448 })
            {
                var data = new byte[len]; new Random(len).NextBytes(data);
                if (Hash32.Calculate(data, len) != Hash32Fast.Calculate(data)) throw new Exception("hash parity");
                if (Hash32.Calculate(data, 0, len) != Hash32Fast.Calculate(data)) throw new Exception("hash parity2");
                H.Measure($"Hash32.Calculate(Span,len) len={len}", 5_000_000, n => { uint s = 0; for (int i = 0; i < n; i++) s += Hash32.Calculate(data, len); GC.KeepAlive(s); });
                H.Measure($"Hash32.Calculate(byte[],off,len) len={len}", 5_000_000, n => { uint s = 0; for (int i = 0; i < n; i++) s += Hash32.Calculate(data, 0, len); GC.KeepAlive(s); });
                H.Measure($"Hash32Fast (MemoryMarshal.Cast + Vector128) len={len}", 5_000_000, n => { uint s = 0; for (int i = 0; i < n; i++) s += Hash32Fast.Calculate(data); GC.KeepAlive(s); });
            }
            {
                var h = new PacketHeader { Sequence = 5, Flags = PacketHeaderFlags.EncryptedChecksum, Id = 7, Time = 9, Size = 100, Iteration = 1 };
                H.Measure("PacketHeader.CalculateHash32 (stackalloc+Pack+Hash32)", 5_000_000, n => { uint s = 0; for (int i = 0; i < n; i++) s += h.CalculateHash32(); GC.KeepAlive(s); });
            }

            H.Header("Logging guard cost: packetLog.IsEnabled(Debug) with the production Log4NetBridgeProvider, Packets=INFO");
            {
                var repo = log4net.LogManager.CreateRepository("bench");
                var xml = new System.Xml.XmlDocument();
                xml.LoadXml("<log4net><logger name=\"Packets\"><level value=\"INFO\" /></logger><root><level value=\"ALL\" /></root></log4net>");
                log4net.Config.XmlConfigurator.Configure(repo, xml.DocumentElement);
                var factory = new LoggerFactory();
                factory.AddProvider(new ACE.Server.Logging.Log4NetBridgeProvider(repo));
                var packetLog = factory.CreateLogger("Packets");
                if (packetLog.IsEnabled(LogLevel.Debug) || !packetLog.IsEnabled(LogLevel.Information)) throw new Exception("log config");
                H.Measure("packetLog.IsEnabled(LogLevel.Debug) [false]", 5_000_000, n => { int c = 0; for (int i = 0; i < n; i++) if (packetLog.IsEnabled(LogLevel.Debug)) c++; GC.KeepAlive(c); });
                var other = factory.CreateLogger("ACE.Server.Network.Managers.NetworkManager");
                H.Measure("log.LogDebug(\"..{EndPoint}..{Id}\", ep, (ushort)id) unguarded, root=ALL so ENABLED", 200_000, n => { var ep = new System.Net.IPEndPoint(0x0100007F, 9000); for (int i = 0; i < n; i++) other.LogDebug("Unsolicited Packet from {EndPoint} with Id {Id}", ep, (ushort)i); });
            }

            H.Header("Inbound dispatch: InboundMessageManager.HandleClientMessage closure vs one action object");
            {
                var handlers = new Dictionary<uint, DispatchInfo> { [0xF7B1] = Info };
                var msg = new ClientMessage(new byte[] { 0xB1, 0xF7, 0, 0, 1, 2, 3, 4 });
                object sess = new object();
                H.Measure("legacy: display class + Action + ActionEventDelegate", 1_000_000, n => { for (int i = 0; i < n; i++) HandleClientMessageLegacy(msg, sess, handlers); Queue.Clear(); });
                H.Measure("proposed: sealed InboundMessageAction", 1_000_000, n => { for (int i = 0; i < n; i++) HandleClientMessageNew(msg, sess, handlers); Queue.Clear(); });
            }

            H.Header("EnqueueSend(params GameMessage[]) per call site / per broadcast recipient");
            {
                var r = new Recv(); var m = new TestMessage(60, GameMessageGroup.SmartboxQueue, 1);
                H.Measure("EnqueueSend(msg) -> params GameMessage[] (today)", 5_000_000, n => { for (int i = 0; i < n; i++) r.EnqueueSend(m); });
                H.Measure("EnqueueSend(msg) -> params ReadOnlySpan<GameMessage> (C# 13)", 5_000_000, n => { for (int i = 0; i < n; i++) r.EnqueueSendSpan(m); });
                H.Measure("EnqueueSend(msg) -> single-message overload", 5_000_000, n => { for (int i = 0; i < n; i++) r.EnqueueSendOne(m); });
            }

            H.Header("NetworkBundle swap: new bundle per flush vs reuse (bundle with 3 messages)");
            {
                var m = new TestMessage(60, GameMessageGroup.SmartboxQueue, 1);
                H.Measure("new LNetworkBundle + 3 Enqueue (today: currentBundles[i] = new NetworkBundle())", 2_000_000, n => { for (int i = 0; i < n; i++) { var b = new LNetworkBundle(); b.Enqueue(m); b.Enqueue(m); b.Enqueue(m); } });
                var spare = new LNetworkBundle();
                H.Measure("reused bundle: Reset + 3 Enqueue", 2_000_000, n => { for (int i = 0; i < n; i++) { spare.Reset(); spare.Enqueue(m); spare.Enqueue(m); spare.Enqueue(m); while (spare.HasMoreMessages) spare.Dequeue(); } });
            }

            H.Header("Retransmit cache: per-ACK cost with K cached packets (ack removes 4, 4 added per ack)");
            foreach (int K in new[] { 40, 400, 2400 })
            {
                var obj = new object();
                {
                    var cd = new ConcurrentDictionary<uint, object>(); uint next = 1; for (int i = 0; i < K; i++) cd.TryAdd(next++, obj);
                    uint ackPoint = 1;
                    H.Measure($"K={K,4} legacy: cachedPackets.Keys.Where(x<seq) + TryRemove, + 4 TryAdd", 20_000, n => { for (int i = 0; i < n; i++) { for (int a = 0; a < 4; a++) cd.TryAdd(next++, obj); ackPoint += 4; foreach (var key in cd.Keys.Where(x => x < ackPoint)) cd.TryRemove(key, out _); } }, runs: 3);
                }
                {
                    var cd = new ConcurrentDictionary<uint, object>(); uint next = 1; for (int i = 0; i < K; i++) cd.TryAdd(next++, obj);
                    uint ackPoint = 1;
                    H.Measure($"K={K,4} P.0d fix: foreach (kv in cachedPackets) TryRemove, + 4 TryAdd", 20_000, n => { for (int i = 0; i < n; i++) { for (int a = 0; a < 4; a++) cd.TryAdd(next++, obj); ackPoint += 4; foreach (var kv in cd) if (kv.Key < ackPoint) cd.TryRemove(kv.Key, out _); } }, runs: 3);
                }
                {
                    var cd = new ConcurrentDictionary<uint, object>(); uint next = 1; for (int i = 0; i < K; i++) cd.TryAdd(next++, obj);
                    uint ackPoint = 1, lowest = 1;
                    H.Measure($"K={K,4} seq-walk: for (s=lowest; s<seq; s++) TryRemove(s), + 4 TryAdd", 20_000, n => { for (int i = 0; i < n; i++) { for (int a = 0; a < 4; a++) cd.TryAdd(next++, obj); ackPoint += 4; for (; lowest < ackPoint; lowest++) cd.TryRemove(lowest, out _); } }, runs: 3);
                }
                {
                    var ring = new RetransmitRing<object>(4096); uint next = 1; for (int i = 0; i < K; i++) ring.Add(next++, obj);
                    uint ackPoint = 1;
                    H.Measure($"K={K,4} RetransmitRing (lock, array indexed by seq), + 4 Add", 20_000, n => { for (int i = 0; i < n; i++) { for (int a = 0; a < 4; a++) ring.Add(next++, obj); ackPoint += 4; ring.Ack(ackPoint); } }, runs: 3);
                }
            }

            H.Header("NetworkStatistics: global Interlocked.Increment from parallel tick workers (false/true sharing)");
            {
                foreach (int threads in new[] { 1, 2, 4 })
                {
                    H.Measure($"S2C_Packets_Aggregate_Increment, {threads} threads x n/threads", 4_000_000, n =>
                    {
                        var ts = new Thread[threads];
                        for (int t = 0; t < threads; t++) { ts[t] = new Thread(() => { for (int i = 0; i < n / threads; i++) NetworkStatistics.S2C_Packets_Aggregate_Increment(); }); ts[t].Start(); }
                        foreach (var t in ts) t.Join();
                    }, runs: 3);
                }
            }
        }
    }
}
