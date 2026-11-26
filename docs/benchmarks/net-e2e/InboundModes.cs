using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using ACE.Common.Cryptography;
using ACE.Server.Network;
using ACE.Server.Network.Managers;

namespace NetE2E
{
    public static class InboundMode
    {
        public sealed class Batch { public Client C; public byte[] Flat; public int Stride; public int N; }

        /// <summary>A fresh real session plus N consecutive valid datagrams for it (built outside any measurement).</summary>
        public static Batch Build(InKind kind, int n, int cachedK)
        {
            var c = Env.NewClient(smallRcv: true);
            c.S.TickOutbound(); Env.PushTimers(c);
            if (cachedK > 0)
            {
                var msg = new[] { Sc.Chat(60) };
                while (c.Cache.Count < cachedK) { c.Net.EnqueueSend(msg); Acc.NextSend(c.Net) = default; c.Net.Update(); }
            }
            var tmp = new byte[1024];
            int stride = c.BuildC2S(kind, 1, tmp, 0);
            // rebuild from scratch so the ISAAC stream and sequences start clean: the probe above consumed one key/sequence
            Env.Remove(c);
            c = Env.NewClient(smallRcv: true);
            c.S.TickOutbound(); Env.PushTimers(c);
            if (cachedK > 0)
            {
                var msg = new[] { Sc.Chat(60) };
                while (c.Cache.Count < cachedK) { c.Net.EnqueueSend(msg); Acc.NextSend(c.Net) = default; c.Net.Update(); }
            }
            var flat = new byte[(long)stride * n];
            for (int i = 0; i < n; i++)
            {
                int sz = c.BuildC2S(kind, 1, flat, i * stride);   // ack value 1: acknowledges nothing, so the cache size stays K
                if (sz != stride) throw new Exception("stride");
            }
            return new Batch { C = c, Flat = flat, Stride = stride, N = n };
        }

        public static void Run(string[] args)
        {
            int n = (int)U.Arg(args, "n", 100_000);
            U.H($"(e) INBOUND on the REAL path: new ClientPacket + Unpack + NetworkManager.ProcessPacket(listener, packet, endPoint) + ReleaseBuffer, n={n} datagrams per run, fresh real session per run");
            Console.WriteLine("    = ConnectionListener.OnDataReceive lines 119-124 without the socket. 'docs method' = Unpack + VerifyCRC(standalone CryptoSystem) + new ClientMessage per fragment (what NT.6 measured), same datagrams, same run.");
            Console.WriteLine("    'queue run' = NetworkManager.InboundMessageQueue.RunActions() cost per dispatched message (world thread; handler body returns at once because session.Player == null).");
            Console.WriteLine($"{"datagram",-44} {"size",5} | {"docs method",28} | {"REAL full path",28} | {"queue run / msg",24} | docs NT.6");
            var rows = new (InKind kind, int k, string name, string doc)[]
            {
                (InKind.AckClear,    0,  "ack-only, cleartext CRC (pure ACK)",        "-"),
                (InKind.AckEnc,      0,  "ack-only, encrypted",                       " 736 B /  76 ns"),
                (InKind.AckEnc,      40, "ack-only, encrypted, 40 packets cached",    "(736 B /  76 ns)"),
                (InKind.Action64,    0,  "one action fragment (64 B GameAction)",     " 824 B / 125 ns"),
                (InKind.AckAction64, 0,  "ack + action",                              "1104 B / 144 ns"),
                (InKind.AckAction64, 40, "ack + action, 40 packets cached",           "(1104 B / 144 ns)"),
                (InKind.Ping12,      0,  "one action fragment (12 B PingRequest)",    "-"),
            };
            var rx = new byte[ClientPacket.MaxPacketSize];
            foreach (var row in rows)
            {
                Runs docs = new(), real = new(), q = new();
                int stride = 0;
                for (int r = -3; r < 5; r++)   // r < 0: warm-up rounds on throwaway sessions (with 300 ms sleeps)
                {
                    int cnt = r < 0 ? Math.Max(20000, n / 4) : n;
                    var b = Build(row.kind, cnt, row.k);
                    var c = b.C; stride = b.Stride;
                    long crc0 = NetworkStatistics.C2S_CRCErrors_Aggregate, pk0 = NetworkStatistics.C2S_Packets_Aggregate;

                    // docs method, on a standalone CryptoSystem seeded like the session's
                    {
                        var cs = new CryptoSystem(c.ClientSeed);
                        long t = 0, bytes = 0;
                        for (int i = 0; i < cnt; i++)
                        {
                            Buffer.BlockCopy(b.Flat, i * stride, rx, 0, stride);
                            long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                            var p = new ClientPacket();
                            if (!p.Unpack(rx, stride) || !p.VerifyCRC(cs)) throw new Exception("docs-method verify failed");
                            foreach (var f in p.Fragments) { var m = new ClientMessage(f.Data); }
                            p.ReleaseBuffer();
                            long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                            t += t1 - t0; bytes += a1 - a0;
                        }
                        if (r >= 0) docs.Add(U.Ns(t) / cnt, (double)bytes / cnt);
                    }
                    // REAL full path
                    {
                        long t = 0, bytes = 0, tq = 0, bq = 0; int msgs = 0;
                        var ep = c.Ep; var l0 = Env.L0; var queue = NetworkManager.InboundMessageQueue;
                        for (int i = 0; i < cnt; i++)
                        {
                            Buffer.BlockCopy(b.Flat, i * stride, rx, 0, stride);
                            long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                            var packet = new ClientPacket();
                            if (packet.Unpack(rx, stride))
                                NetworkManager.ProcessPacket(l0, packet, ep);
                            packet.ReleaseBuffer();
                            long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                            t += t1 - t0; bytes += a1 - a0;
                            if ((i & 15) == 15)
                            {
                                a0 = GC.GetAllocatedBytesForCurrentThread(); t0 = Stopwatch.GetTimestamp();
                                queue.RunActions();
                                t1 = Stopwatch.GetTimestamp(); a1 = GC.GetAllocatedBytesForCurrentThread();
                                tq += t1 - t0; bq += a1 - a0; msgs += 16;
                            }
                        }
                        queue.RunActions();
                        bool isAction = row.kind is InKind.Action64 or InKind.AckAction64 or InKind.Ping12;
                        if (NetworkStatistics.C2S_CRCErrors_Aggregate != crc0) throw new Exception("CRC errors on the real path");
                        if (NetworkStatistics.C2S_Packets_Aggregate - pk0 != cnt) throw new Exception("packet count");
                        if (row.kind != InKind.AckClear && Acc.LastRecvSeq(c.Net) != (uint)cnt + 1) throw new Exception($"lastReceivedPacketSequence {Acc.LastRecvSeq(c.Net)} != {cnt + 1}");
                        if (isAction && Acc.LastRecvFragSeq(c.Net) != (uint)cnt) throw new Exception($"lastReceivedFragmentSequence {Acc.LastRecvFragSeq(c.Net)} != {cnt}");
                        if (c.Cache.Count != (row.k == 0 ? 1 : row.k)) throw new Exception($"cache count {c.Cache.Count}");
                        if (r >= 0) { real.Add(U.Ns(t) / cnt, (double)bytes / cnt); if (isAction) q.Add(U.Ns(tq) / msgs, (double)bq / msgs); }
                    }
                    Env.Remove(c);
                    if (r < 0) Thread.Sleep(300);
                }
                Console.WriteLine($"{row.name,-44} {stride,5} | {docs.Fmt(),28} | {real.Fmt(),28} | {(q.Ns.Count > 0 ? q.Fmt() : ""),24} | {row.doc}");
            }
            Console.WriteLine("note: with 0 'cached' the session still holds its one TimeSync packet (sequence 2) in cachedPackets, as a real session does.");
            Console.WriteLine($"note: every ns figure above includes the measurement bracket itself: {U.BracketNs():F1} ns (subtract it for absolute values; it cancels in differences).");
        }
    }

    public static class AckMode
    {
        public static void Run(string[] args)
        {
            int n = (int)U.Arg(args, "n", 20000);
            U.H($"(f) REAL NetworkSession.AcknowledgeSequence(seq) (private; called directly through an accessor) with K packets in the real cachedPackets, n={n} acks per run");
            Console.WriteLine("    Between acks, 4 (or 1, or 0) REAL packets are flushed through the real session (outside the timed region), and the ack removes that many.");
            Console.WriteLine("    COPY = docs Micro.cs shape on ConcurrentDictionary<uint, object>: (a) the scan only, timed like REAL; (b) the docs loop as written (4 TryAdd + scan in the timed region).");
            Console.WriteLine($"{"K cached",9} {"removed/ack",11} | {"REAL AcknowledgeSequence",34} | {"COPY scan only",34} | {"COPY docs loop (4 TryAdd + scan)",36} | docs NT.7 'Keys.Where (today)'");
            Console.WriteLine($"    (REAL and 'COPY scan only' ns figures include the measurement bracket: {U.BracketNs():F1} ns)");
            var msg = new[] { Sc.Chat(60) };
            foreach (int K in new[] { 10, 40, 400, 2400 })
                foreach (int per in new[] { 4, 1, 0 })
                {
                    var c = Env.NewClient(smallRcv: true);
                    c.S.TickOutbound(); Env.PushTimers(c);
                    var net = c.Net;
                    while (c.Cache.Count < K) { net.EnqueueSend(msg); Acc.NextSend(net) = default; net.Update(); }
                    long t = 0, bytes = 0;
                    Action<int> body = cnt =>
                    {
                        for (int i = 0; i < cnt; i++)
                        {
                            for (int a = 0; a < per; a++) { net.EnqueueSend(msg); Acc.NextSend(net) = default; net.Update(); }
                            uint ackPoint = c.CurSeq - (uint)K + 1;
                            long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                            Acc.Ack(net, ackPoint);
                            long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                            t += t1 - t0; bytes += a1 - a0;
                        }
                    };
                    int nn = K >= 2400 ? n / 4 : n;
                    U.Warm(() => body(nn / 5), 3);
                    var real = new Runs();
                    for (int r = 0; r < 5; r++) { t = bytes = 0; body(nn); real.Add(U.Ns(t) / nn, (double)bytes / nn); }
                    if (c.Cache.Count != K) throw new Exception($"cache count {c.Cache.Count} != {K}");
                    Env.Remove(c);

                    // COPY (a): scan only
                    var obj = new object();
                    var cd = new ConcurrentDictionary<uint, object>(); uint next = 1; for (int i = 0; i < K; i++) cd.TryAdd(next++, obj);
                    long tc = 0, bc = 0;
                    Action<int> copyBody = cnt =>
                    {
                        for (int i = 0; i < cnt; i++)
                        {
                            for (int a = 0; a < per; a++) cd.TryAdd(next++, obj);
                            uint ackPoint = next - (uint)K;
                            long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                            var removalList = cd.Keys.Where(x => x < ackPoint);
                            foreach (var key in removalList) cd.TryRemove(key, out _);
                            long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                            tc += t1 - t0; bc += a1 - a0;
                        }
                    };
                    U.Warm(() => copyBody(nn / 5), 3);
                    var copy = new Runs();
                    for (int r = 0; r < 5; r++) { tc = bc = 0; copyBody(nn); copy.Add(U.Ns(tc) / nn, (double)bc / nn); }

                    // COPY (b): the docs loop as written
                    string docsLoop = "";
                    if (per == 4)
                    {
                        var cd2 = new ConcurrentDictionary<uint, object>(); uint next2 = 1; for (int i = 0; i < K; i++) cd2.TryAdd(next2++, obj);
                        uint ackPoint2 = 1;
                        Action<int> docsBody = cnt => { for (int i = 0; i < cnt; i++) { for (int a = 0; a < 4; a++) cd2.TryAdd(next2++, obj); ackPoint2 += 4; foreach (var key in cd2.Keys.Where(x => x < ackPoint2)) cd2.TryRemove(key, out _); } };
                        U.Warm(() => docsBody(nn / 5), 3);
                        var dr = new Runs();
                        for (int r = 0; r < 5; r++) { long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp(); docsBody(nn); long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread(); dr.Add(U.Ns(t1 - t0) / nn, (double)(a1 - a0) / nn); }
                        docsLoop = dr.Fmt();
                    }
                    string doc = per != 4 ? "" : K == 40 ? "476 ns, 472 B" : K == 400 ? "1.7 us, 1.9 KB" : K == 2400 ? "6.8 us, 9.9 KB" : "(not measured)";
                    Console.WriteLine($"{K,9} {per,11} | {real.Fmt(),34} | {copy.Fmt(),34} | {docsLoop,36} | {doc}");
                }
        }
    }
}
