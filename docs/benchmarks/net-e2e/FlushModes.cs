using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace NetE2E
{
    public sealed class Scenario
    {
        public string Name; public GameMessage[] Msgs; public bool Ack, Echo; public string Doc;
        public GameMessageGroup Group => Msgs.Length > 0 ? Msgs[0].Group : GameMessageGroup.InvalidQueue;
    }

    public static class Sc
    {
        /// <summary>A real GameMessageSystemChat whose serialised length is exactly totalLen (opcode 4 + len16 2 + text + pad + type 4).</summary>
        public static GameMessage Chat(int totalLen)
        {
            var m = new GameMessageSystemChat(new string('x', totalLen - 10), ChatMessageType.Broadcast);
            if (m.Data.Length != totalLen) throw new Exception($"chat len {m.Data.Length} != {totalLen}");
            return m;
        }

        public static Scenario[] All()
        {
            return new[]
            {
                new Scenario { Name = "1 x 60 B message",        Msgs = new[] { Chat(60) }, Doc = " 858 B / 245 ns" },
                new Scenario { Name = "6 small (60,44,80,52,72,40)", Msgs = new[] { Chat(60), Chat(44), Chat(80), Chat(52), Chat(72), Chat(40) }, Doc = "2146 B / 490 ns" },
                new Scenario { Name = "ack + echo only",         Msgs = new GameMessage[0], Ack = true, Echo = true, Doc = " 482 B / 123 ns" },
                new Scenario { Name = "ack + echo + 3 small (60,48,64)", Msgs = new[] { Chat(60), Chat(48), Chat(64) }, Ack = true, Echo = true, Doc = "1386 B / 302 ns" },
                new Scenario { Name = "1500 B + 3 small (60,92,40)", Msgs = new[] { Chat(1500), Chat(60), Chat(92), Chat(40) }, Doc = "3938 B / 809 ns" },
                new Scenario { Name = "pure ack only (2 s timer)", Msgs = new GameMessage[0], Ack = true, Doc = "(not in docs)" },
            };
        }

        /// <summary>Queue the scenario's work on a real session (public EnqueueSend; FlagEcho and the ack timer by accessor).</summary>
        public static void Arm(Client c, Scenario sc)
        {
            if (sc.Msgs.Length > 0) c.Net.EnqueueSend(sc.Msgs);
            if (sc.Echo) Acc.FlagEcho(c.Net, 1234.5f);       // what an inbound EchoRequest does (NetworkSession.cs:452)
            if (sc.Ack) Acc.NextAck(c.Net) = default;        // make the 2 s ack timer due
        }

        /// <summary>Calls the real Update() until everything armed has been sent (one group per call because of the 5 ms gate); returns the number of Update calls that sent something.</summary>
        public static int FlushAll(Client c, Scenario sc)
        {
            int updates = (sc.Ack || sc.Echo ? 1 : 0) + (sc.Msgs.Length > 0 ? 1 : 0);
            for (int u = 0; u < updates; u++) { Acc.NextSend(c.Net) = default; c.Net.Update(); }
            return updates;
        }
    }

    /// <summary>docs LegacySession sink that performs the REAL send: same socket, same overload, same destination as NetworkSession.SendPacketRaw.</summary>
    public sealed class SocketSink : Bench.ISink
    {
        public Socket Sock; public EndPoint Dst;
        public void Send(byte[] b, int size) { Sock.SendTo(b, size, SocketFlags.None, Dst); }
    }

    public static class RUsage
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RU { public long uSec, uUsec, sSec, sUsec; public long a, b, c, d, e, f, g, h, i, j, k, l, m, n; }
        [System.Runtime.InteropServices.DllImport("libc")] static extern int getrusage(int who, out RU ru);
        /// <summary>(user ns, kernel ns) consumed by the calling thread so far.</summary>
        public static (double user, double sys) Thread() { getrusage(1 /* RUSAGE_THREAD */, out var r); return (r.uSec * 1e9 + r.uUsec * 1e3, r.sSec * 1e9 + r.sUsec * 1e3); }
    }

    public static class FlushMode
    {
        public static void Run(string[] args)
        {
            int n = (int)U.Arg(args, "n", 30000);
            bool drain = U.Arg(args, "drain", 0) != 0;
            U.H($"(b) FLUSH on the REAL NetworkSession: EnqueueSend + Update() (real SendBundle/FlushPackets/SendPacketRaw/Socket.SendTo), n={n} per run, 7 runs, client {(drain ? "DRAINED each flush" : "not draining (kernel drops)")}");
            Console.WriteLine("    'real total' = EnqueueSend + every Update() call needed to put the scenario on the wire. 'sendto' = the same datagrams sent by a bare");
            Console.WriteLine("    Socket.SendTo(byte[],int,SocketFlags,EndPoint) on the same listener socket to the same client, interleaved 1:1 with the real flushes.");
            Console.WriteLine("    'COPY' = docs LegacySession (verbatim copy, NullSink) fed the SAME message objects in the same run, measured the docs way (new bundle + Fill + SendBundle + FlushPackets).");
            foreach (var sc in Sc.All())
                One(sc, n, drain);

            SendToRef();
        }

        static void One(Scenario sc, int n, bool drain)
        {
            var c = Env.NewClient(smallRcv: false);
            var net = c.Net;
            c.S.TickOutbound(); Env.PushTimers(c); c.Drain();

            // dry run: learn what really goes on the wire
            Sc.Arm(c, sc); int updates = Sc.FlushAll(c, sc);
            var wire = c.Drain();
            int[] sizes = wire.Select(d => d.Size).ToArray();
            Console.WriteLine();
            Console.WriteLine($"--- {sc.Name}   [{updates} Update() call(s) -> {wire.Count} datagram(s): {string.Join(" | ", wire.Select(d => $"{d.Size} B {Client.ShortFlags(d.Flags)}{(d.Frags > 0 ? " " + d.Frags + " frag" : "")}"))}]   docs NT.3: {sc.Doc}");

            var sock = Env.L1.Socket; var dst = c.S.EndPointS2C; var refBuf = new byte[1024]; var rxb = new byte[2048];
            uint trimLo = 2;
            long tE = 0, bE = 0, tU = 0, bU = 0, tS = 0, bS = 0, tH = 0, bH = 0;
            var hs = new Bench.LegacySession { Sink = new SocketSink { Sock = sock, Dst = dst } };
            var hgrp = sc.Group;
            Action<int> body = cnt =>
            {
                for (int i = 0; i < cnt; i++)
                {
                    // ---- enqueue (bracket E) ----
                    long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                    if (sc.Msgs.Length > 0) net.EnqueueSend(sc.Msgs);
                    long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                    tE += t1 - t0; bE += a1 - a0;
                    if (sc.Echo) Acc.FlagEcho(net, 1234.5f);
                    if (sc.Ack) Acc.NextAck(net) = default;
                    // ---- real Update() (bracket U), one call per group that has something ----
                    for (int u = 0; u < updates; u++)
                    {
                        Acc.NextSend(net) = default;
                        a0 = GC.GetAllocatedBytesForCurrentThread(); t0 = Stopwatch.GetTimestamp();
                        net.Update();
                        t1 = Stopwatch.GetTimestamp(); a1 = GC.GetAllocatedBytesForCurrentThread();
                        tU += t1 - t0; bU += a1 - a0;
                    }
                    // ---- in-situ sendto reference (bracket S): same sizes, same socket, same destination ----
                    a0 = GC.GetAllocatedBytesForCurrentThread(); t0 = Stopwatch.GetTimestamp();
                    for (int k = 0; k < sizes.Length; k++) sock.SendTo(refBuf, sizes[k], SocketFlags.None, dst);
                    t1 = Stopwatch.GetTimestamp(); a1 = GC.GetAllocatedBytesForCurrentThread();
                    tS += t1 - t0; bS += a1 - a0;
                    // ---- docs copy with a REAL send (bracket H): one bundle, as the docs modelled it ----
                    a0 = GC.GetAllocatedBytesForCurrentThread(); t0 = Stopwatch.GetTimestamp();
                    {
                        var hb = new Bench.LNetworkBundle();
                        hb.EncryptedChecksum = sc.Msgs.Length > 0 || sc.Echo;
                        foreach (var m in sc.Msgs) hb.Enqueue(m);
                        if (sc.Ack) hb.SendAck = true;
                        if (sc.Echo) hb.ClientTime = 1234.5f;
                        hs.SendBundle(hb, hgrp); hs.FlushPackets();
                    }
                    t1 = Stopwatch.GetTimestamp(); a1 = GC.GetAllocatedBytesForCurrentThread();
                    tH += t1 - t0; bH += a1 - a0;
                    if ((i & 31) == 0) { uint lo = hs.PacketSequence - 8; foreach (var kv in hs.cachedPackets) if (kv.Key < lo) hs.cachedPackets.TryRemove(kv.Key, out _); }

                    if (drain) while (c.Sock.Available > 0) c.Sock.Receive(rxb);
                    if ((i & 31) == 0) { uint cur = c.CurSeq; while (trimLo + 8 < cur) c.Cache.TryRemove(trimLo++, out _); }
                }
            };
            U.Warm(() => body(Math.Max(2000, n / 6)));
            Runs rE = new(), rU = new(), rS = new(), rTot = new(), rNet = new(), rH = new();
            for (int r = 0; r < 7; r++)
            {
                tE = bE = tU = bU = tS = bS = tH = bH = 0;
                body(n);
                rH.Add(U.Ns(tH) / n, (double)bH / n);
                rE.Add(U.Ns(tE) / n, (double)bE / n); rU.Add(U.Ns(tU) / n, (double)bU / n); rS.Add(U.Ns(tS) / n, (double)bS / n);
                rTot.Add(U.Ns(tE + tU) / n, (double)(bE + bU) / n);
                rNet.Add(U.Ns(tE + tU - tS) / n, (double)(bE + bU - bS) / n);
            }
            Console.WriteLine($"    REAL EnqueueSend(msgs[])                 {rE.Fmt()}");
            Console.WriteLine($"    REAL Update() x{updates} (incl. sendto)          {rU.Fmt()}");
            Console.WriteLine($"    REAL total per scenario (incl. sendto)   {rTot.Fmt()}");
            Console.WriteLine($"    bare sendto x{sizes.Length} (in situ)                 {rS.Fmt()}");
            Console.WriteLine($"    REAL total MINUS sendto                  {rNet.Fmt()}");
            Console.WriteLine($"    COPY docs LegacySession + REAL sendto    {rH.Fmt()}     (one bundle => {(sc.Msgs.Length > 0 && (sc.Ack || sc.Echo) ? "1 datagram where the real code sends 2" : "same datagrams")})");

            // ---- user/kernel split of the calling thread (getrusage(RUSAGE_THREAD)), REAL-only loop and COPY+send-only loop ----
            {
                var (u0, s0) = RUsage.Thread(); long w0 = Stopwatch.GetTimestamp();
                int tot = n * 4;
                for (int i = 0; i < tot; i++)
                {
                    if (sc.Msgs.Length > 0) net.EnqueueSend(sc.Msgs);
                    if (sc.Echo) Acc.FlagEcho(net, 1234.5f);
                    if (sc.Ack) Acc.NextAck(net) = default;
                    for (int u = 0; u < updates; u++) { Acc.NextSend(net) = default; net.Update(); }
                    if ((i & 31) == 0) { uint cur = c.CurSeq; while (trimLo + 8 < cur) c.Cache.TryRemove(trimLo++, out _); }
                }
                long w1 = Stopwatch.GetTimestamp(); var (u1, s1) = RUsage.Thread();
                Console.WriteLine($"    REAL-only loop, thread CPU per scenario:        user {(u1 - u0) / tot,7:F0} ns + kernel {(s1 - s0) / tot,7:F0} ns   (wall {U.Ns(w1 - w0) / tot,7:F0} ns)");
                (u0, s0) = RUsage.Thread(); w0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < tot; i++)
                {
                    var hb = new Bench.LNetworkBundle();
                    hb.EncryptedChecksum = sc.Msgs.Length > 0 || sc.Echo;
                    foreach (var m in sc.Msgs) hb.Enqueue(m);
                    if (sc.Ack) hb.SendAck = true;
                    if (sc.Echo) hb.ClientTime = 1234.5f;
                    hs.SendBundle(hb, hgrp); hs.FlushPackets();
                    if ((i & 31) == 0) { uint lo = hs.PacketSequence - 8; foreach (var kv in hs.cachedPackets) if (kv.Key < lo) hs.cachedPackets.TryRemove(kv.Key, out _); }
                }
                w1 = Stopwatch.GetTimestamp(); (u1, s1) = RUsage.Thread();
                Console.WriteLine($"    COPY+REAL-sendto-only loop, thread CPU:         user {(u1 - u0) / tot,7:F0} ns + kernel {(s1 - s0) / tot,7:F0} ns   (wall {U.Ns(w1 - w0) / tot,7:F0} ns)");
                (u0, s0) = RUsage.Thread(); w0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < tot; i++)
                    for (int k = 0; k < sizes.Length; k++) sock.SendTo(refBuf, sizes[k], SocketFlags.None, dst);
                w1 = Stopwatch.GetTimestamp(); (u1, s1) = RUsage.Thread();
                Console.WriteLine($"    bare-sendto-only loop, thread CPU:              user {(u1 - u0) / tot,7:F0} ns + kernel {(s1 - s0) / tot,7:F0} ns   (wall {U.Ns(w1 - w0) / tot,7:F0} ns)");
            }

            // ---- docs copy in the same run, same message objects ----
            var ls = new Bench.LegacySession { Sink = new Bench.NullSink() };
            var grp = sc.Group;
            long tC = 0, bC = 0;
            Action<int> copyBody = cnt =>
            {
                for (int i = 0; i < cnt; i++)
                {
                    long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                    var b = new Bench.LNetworkBundle();
                    b.EncryptedChecksum = sc.Msgs.Length > 0 || sc.Echo;
                    foreach (var m in sc.Msgs) b.Enqueue(m);
                    if (sc.Ack) b.SendAck = true;
                    if (sc.Echo) b.ClientTime = 1234.5f;
                    ls.SendBundle(b, grp); ls.FlushPackets();
                    long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                    tC += t1 - t0; bC += a1 - a0;
                    if ((i & 31) == 0) { uint lo = ls.PacketSequence - 8; foreach (var kv in ls.cachedPackets) if (kv.Key < lo) ls.cachedPackets.TryRemove(kv.Key, out _); }
                }
            };
            U.Warm(() => copyBody(Math.Max(2000, n / 6)), 3);
            var rC = new Runs();
            for (int r = 0; r < 7; r++) { tC = bC = 0; copyBody(n); rC.Add(U.Ns(tC) / n, (double)bC / n); }
            Console.WriteLine($"    COPY docs LegacySession, NullSink (docs) {rC.Fmt()}");
            Console.WriteLine($"    => bytes REAL-minus-sendto / COPY = {rNet.Bytes / rC.Bytes:F2}   ns REAL-minus-sendto / COPY = {rNet.Best / rC.Best:F2}   ns REAL total / COPY = {rTot.Best / rC.Best:F1}");
            Env.Remove(c);
        }

        /// <summary>Bare sendto cost on the real listener socket, for the three client states.</summary>
        static void SendToRef()
        {
            U.H("sendto reference on the real S2C listener socket (Socket.SendTo(byte[],int,SocketFlags,EndPoint), the overload NetworkSession uses)");
            var sock = Env.L1.Socket; var buf = new byte[1024]; var rxb = new byte[2048];
            foreach (int size in new[] { 24, 96, 464 })
            {
                var cDrop = Env.NewClient(smallRcv: true);
                var cDrain = Env.NewClient(smallRcv: false);
                Runs drop = new(), drained = new();
                const int n = 30000;
                Action<Client, Runs, bool, bool> go = (c, runs, dr, record) =>
                {
                    var dst = c.S.EndPointS2C; long t = 0, b = 0;
                    for (int i = 0; i < n; i++)
                    {
                        long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                        sock.SendTo(buf, size, SocketFlags.None, dst);
                        long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                        t += t1 - t0; b += a1 - a0;
                        if (dr) while (c.Sock.Available > 0) c.Sock.Receive(rxb);
                    }
                    if (record) runs.Add(U.Ns(t) / n, (double)b / n);
                };
                U.Warm(() => { go(cDrop, drop, false, false); go(cDrain, drained, true, false); }, 2);
                for (int r = 0; r < 5; r++) { go(cDrop, drop, false, true); go(cDrain, drained, true, true); }
                Console.WriteLine($"  {size,4} B datagram   client not reading (dropped) {drop.Fmt()}     client drained {drained.Fmt()}");
                Env.Remove(cDrop); Env.Remove(cDrain);
            }
        }
    }

    public static class RetainedMode
    {
        public static void Run(string[] args)
        {
            int K = (int)U.Arg(args, "k", 10000);
            U.H($"(c) RETAINED bytes per cached packet in the REAL cachedPackets: {K} scenario flushes with nothing acknowledged, then every key removed with TryRemove;");
            Console.WriteLine("    retained = (GC.GetTotalMemory(true) with the packets cached) - (the same after removing them), divided by the number of cached packets.");
            Console.WriteLine("    This counts the ServerPacket object graph and the dictionary node, not the dictionary's bucket table (which does not shrink). COPY = docs LegacySession, same method, same messages.");
            Console.WriteLine($"{"scenario",-36} {"cached pkts/flush",18} {"REAL B/cached pkt",18} {"REAL B/flush",13} {"COPY B/cached pkt",18} {"wire B/pkt (avg)",17} {"retained/wire",14}   docs: 358-1174 B per cached packet");
            foreach (var sc in Sc.All())
            {
                // REAL
                var c = Env.NewClient(smallRcv: true);
                c.S.TickOutbound(); Env.PushTimers(c);
                for (int i = 0; i < 64; i++) { Sc.Arm(c, sc); Sc.FlushAll(c, sc); }        // warm: ArrayPool, JIT
                foreach (var k in c.Cache.Keys) c.Cache.TryRemove(k, out _);
                for (int i = 0; i < K; i++) { Sc.Arm(c, sc); Sc.FlushAll(c, sc); }
                int added = c.Cache.Count;
                double wire = added > 0 ? c.Cache.Values.Average(p => p.Header.Size + 20.0) : 0;
                long with = Settle();
                foreach (var k in c.Cache.Keys) c.Cache.TryRemove(k, out _);
                long without = Settle();
                GC.KeepAlive(c);

                // COPY
                var ls = new Bench.LegacySession { Sink = new Bench.NullSink() };
                Action copyFlush = () =>
                {
                    var b = new Bench.LNetworkBundle();
                    b.EncryptedChecksum = sc.Msgs.Length > 0 || sc.Echo;
                    foreach (var m in sc.Msgs) b.Enqueue(m);
                    if (sc.Ack) b.SendAck = true;
                    if (sc.Echo) b.ClientTime = 1234.5f;
                    ls.SendBundle(b, sc.Group); ls.FlushPackets();
                };
                for (int i = 0; i < 64; i++) copyFlush();
                foreach (var k in ls.cachedPackets.Keys) ls.cachedPackets.TryRemove(k, out _);
                for (int i = 0; i < K; i++) copyFlush();
                int cadded = ls.cachedPackets.Count;
                long cwith = Settle();
                foreach (var k in ls.cachedPackets.Keys) ls.cachedPackets.TryRemove(k, out _);
                long cwithout = Settle();
                GC.KeepAlive(ls);

                double per = added > 0 ? (double)(with - without) / added : 0;
                Console.WriteLine($"{sc.Name,-36} {(double)added / K,18:F2} {(added > 0 ? per.ToString("F0") : "n/a (not cached)"),18} {(double)(with - without) / K,13:F0} {(cadded > 1 ? ((double)(cwith - cwithout) / cadded).ToString("F0") : "n/a"),18} {wire,17:F0} {(wire > 0 ? (per / wire).ToString("F1") + "x" : ""),14}");
                Env.Remove(c);
            }
        }

        static long Settle()
        {
            long m = 0;
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(30); m = GC.GetTotalMemory(true); }
            return m;
        }
    }

    public static class BroadcastMode
    {
        public static void Run(string[] args)
        {
            U.H("(d) BROADCAST on the real path: build ONE real message, EnqueueSend it to R real sessions, TickOutbound each (real sendto)");
            Console.WriteLine("    form A = session.Network.EnqueueSend(msgArray) with one shared array (what WorldObject.EnqueueBroadcast(params GameMessage[]) does)");
            Console.WriteLine("    form B = session.Network.EnqueueSend(msg) per recipient (C# params array per call: EnqueueBroadcast(msg, range), PlayerManager broadcasts)");
            const int MaxR = 100;
            var cs = Env.NewClients(MaxR);
            foreach (var c in cs) { c.S.TickOutbound(); Env.PushTimers(c); }
            var wo = Stubs.MakeWorldObject();
            var sock = Env.L1.Socket; var refBuf = new byte[1024];
            var trimLo = new uint[MaxR]; for (int i = 0; i < MaxR; i++) trimLo[i] = 2;

            foreach (string kind in new[] { "GameMessageUpdatePosition (stub WorldObject)", "GameMessageSystemChat 60 B", "GameMessageSound 16 B" })
            {
                Func<GameMessage> make = kind.StartsWith("GameMessageUpdatePosition") ? () => new GameMessageUpdatePosition(wo)
                                       : kind.StartsWith("GameMessageSystemChat") ? () => new GameMessageSystemChat("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", ChatMessageType.Broadcast)
                                       : () => new GameMessageSound(wo.Guid, Sound.Attack1, 1.0f);
                var probe = make();
                int wireSize = 20 + 16 + (int)probe.Data.Length;
                Console.WriteLine();
                Console.WriteLine($"--- {kind}: message {probe.Data.Length} B, group {probe.Group}, datagram {wireSize} B");
                Console.WriteLine($"{"R",4} {"form",5} | {"construct B",11} {"construct ns",12} | {"enqueue B/rcpt",14} {"enqueue ns/rcpt",15} | {"tick B/rcpt",11} {"tick ns/rcpt",12} | {"sendto ns/rcpt",14} | {"per rcpt EXCL construct: B",26} {"ns",8} {"ns minus sendto",15} | {"per rcpt INCL construct: B",26} {"ns",8}");
                foreach (int R in new[] { 1, 10, 100 })
                    foreach (char form in new[] { 'A', 'B' })
                    {
                        int rounds = Math.Max(300, 30000 / R);
                        long tC = 0, bC = 0, tE = 0, bE = 0, tT = 0, bT = 0, tS = 0;
                        Action<int> body = cnt =>
                        {
                            for (int it = 0; it < cnt; it++)
                            {
                                long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                                var msg = make();
                                long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread();
                                tC += t1 - t0; bC += a1 - a0;

                                a0 = GC.GetAllocatedBytesForCurrentThread(); t0 = Stopwatch.GetTimestamp();
                                if (form == 'A') { var arr = new GameMessage[] { msg }; for (int r = 0; r < R; r++) cs[r].Net.EnqueueSend(arr); }
                                else { for (int r = 0; r < R; r++) cs[r].Net.EnqueueSend(msg); }
                                t1 = Stopwatch.GetTimestamp(); a1 = GC.GetAllocatedBytesForCurrentThread();
                                tE += t1 - t0; bE += a1 - a0;

                                for (int r = 0; r < R; r++) Acc.NextSend(cs[r].Net) = default;

                                a0 = GC.GetAllocatedBytesForCurrentThread(); t0 = Stopwatch.GetTimestamp();
                                for (int r = 0; r < R; r++) cs[r].S.TickOutbound();
                                t1 = Stopwatch.GetTimestamp(); a1 = GC.GetAllocatedBytesForCurrentThread();
                                tT += t1 - t0; bT += a1 - a0;

                                t0 = Stopwatch.GetTimestamp();
                                for (int r = 0; r < R; r++) sock.SendTo(refBuf, wireSize, SocketFlags.None, cs[r].S.EndPointS2C);
                                tS += Stopwatch.GetTimestamp() - t0;

                                if ((it & 15) == 0) for (int r = 0; r < R; r++) { uint cur = cs[r].CurSeq; var cache = cs[r].Cache; while (trimLo[r] + 8 < cur) cache.TryRemove(trimLo[r]++, out _); }
                            }
                        };
                        U.Warm(() => body(Math.Max(100, rounds / 5)), 3);
                        Runs rc = new(), re = new(), rt = new(), rs = new();
                        for (int run = 0; run < 5; run++)
                        {
                            tC = bC = tE = bE = tT = bT = tS = 0;
                            body(rounds);
                            rc.Add(U.Ns(tC) / rounds, (double)bC / rounds);
                            re.Add(U.Ns(tE) / rounds / R, (double)bE / rounds / R);
                            rt.Add(U.Ns(tT) / rounds / R, (double)bT / rounds / R);
                            rs.Add(U.Ns(tS) / rounds / R, 0);
                        }
                        double exB = re.Bytes + rt.Bytes, exNs = re.Med + rt.Med;
                        Console.WriteLine($"{R,4} {form,5} | {rc.Bytes,11:F0} {rc.Med,12:F0} | {re.Bytes,14:F1} {re.Med,15:F1} | {rt.Bytes,11:F1} {rt.Med,12:F0} | {rs.Med,14:F0} | {exB,26:F1} {exNs,8:F0} {exNs - rs.Med,15:F0} | {exB + rc.Bytes / R,26:F1} {exNs + rc.Med / R,8:F0}");
                    }
            }
            foreach (var c in cs) Env.Remove(c);
        }
    }
}
