using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using ACE.Common;
using ACE.Server.Network;
using ACE.Server.Network.Managers;

namespace NetE2E
{
    public static class TickMode
    {
        static Client[] MakeIdle(int n, out Session[] sessions)
        {
            var cs = Env.NewClients(n);
            foreach (var c in cs) c.S.TickOutbound();              // first real tick: the TimeSync bundle goes out (sequence 2)
            foreach (var c in cs) Env.PushTimers(c);               // accessor: nextAck/nextResync far ahead => nothing due
            sessions = cs.Select(c => c.S).ToArray();
            return cs;
        }

        public static void Run(string[] args)
        {
            const int N = 1000;
            var cs = MakeIdle(N, out var sessions);
            long sent0 = NetworkStatistics.S2C_Packets_Aggregate;
            Console.WriteLine($"[tick] {N} real sessions, state={sessions[0].State}, S2C packets so far {sent0} (ConnectRequest + TimeSync each), cached per session {cs[0].Cache.Count}");

            var legacy = new Bench.LegacyTickSession[N];
            for (int i = 0; i < N; i++) legacy[i] = new Bench.LegacyTickSession();
            var lockObj = new object();

            int n = (int)U.Arg(args, "n", 2_000_000);
            U.Warm(() => { for (int i = 0; i < 400_000; i++) sessions[i % N].TickOutbound(); });
            U.Warm(() => { for (int i = 0; i < 400_000; i++) legacy[i % N].TickOutbound(); });
            U.Warm(() => { long s = 0; for (int i = 0; i < 400_000; i++) { s += DateTime.UtcNow.Ticks; lock (lockObj) { s++; } } GC.KeepAlive(s); }, 2);

            Runs real = new(), real1 = new(), copy = new(), copy1 = new(), clock = new(), lck = new();
            for (int r = 0; r < 7; r++)
            {
                { long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) sessions[i % N].TickOutbound(); long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread(); real.Add(U.Ns(t1 - t0) / n, (double)(a1 - a0) / n); }
                { long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) legacy[i % N].TickOutbound(); long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread(); copy.Add(U.Ns(t1 - t0) / n, (double)(a1 - a0) / n); }
                { var s1 = sessions[7]; long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) s1.TickOutbound(); long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread(); real1.Add(U.Ns(t1 - t0) / n, (double)(a1 - a0) / n); }
                { var l1 = legacy[7]; long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) l1.TickOutbound(); long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread(); copy1.Add(U.Ns(t1 - t0) / n, (double)(a1 - a0) / n); }
                { long s = 0; long t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) s += DateTime.UtcNow.Ticks; long t1 = Stopwatch.GetTimestamp(); GC.KeepAlive(s); clock.Add(U.Ns(t1 - t0) / n, 0); }
                { long t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) lock (lockObj) { } long t1 = Stopwatch.GetTimestamp(); lck.Add(U.Ns(t1 - t0) / n, 0); }
            }
            long sent1 = NetworkStatistics.S2C_Packets_Aggregate;
            U.H("(a) IDLE TICK, per session, nothing queued, timers quiesced (ack/resync pushed out by accessor)");
            Console.WriteLine($"REAL  Session.TickOutbound, {N} sessions round-robin          {real.Fmt()}");
            Console.WriteLine($"COPY  docs LegacyTickSession.TickOutbound, {N} round-robin     {copy.Fmt()}");
            Console.WriteLine($"REAL  Session.TickOutbound, ONE session (cache-hot)            {real1.Fmt()}");
            Console.WriteLine($"COPY  docs LegacyTickSession.TickOutbound, ONE session         {copy1.Fmt()}");
            Console.WriteLine($"ref   DateTime.UtcNow                                          {clock.Fmt()}");
            Console.WriteLine($"ref   uncontended lock(obj){{}}                                  {lck.Fmt()}");
            Console.WriteLine($"ratio REAL/COPY (round-robin, best) = {real.Best / copy.Best:F2}   REAL/UtcNow = {real.Best / clock.Best:F1}   (4 UtcNow + 12 locks = {4 * clock.Best + 12 * lck.Best:F0} ns)");
            Console.WriteLine($"check: S2C packets sent during the idle measurement = {sent1 - sent0} (must be 0)");

            // ---------------- DoSessionWork ----------------
            U.H("(a) REAL NetworkManager.DoSessionWork() per CALL, sessions in the real 1000-slot sessionMap");
            var po = ConfigManager.Config.Server.Threading.NetworkManagerParallelOptions;
            Console.WriteLine($"{"layout",-34} {"dop",3} {"ns/call best",14} {"ns/call med",12} {"B/call",9} {"ns/session",11} {"serial loop ns/call",20} {"overhead ns/call",17}");
            foreach (string layout in new[] { "low", "spread" })
                foreach (int active in new[] { 10, 200, 1000 })
                {
                    if (layout == "spread" && active == 1000) continue;
                    Env.ClearMap();
                    var act = new Session[active];
                    for (int i = 0; i < active; i++)
                    {
                        // "low": slots 0..active-1 (what FindOrCreateSession produces). "spread": every (1000/active)-th slot (docs layout).
                        // The session keeps its own ClientId; DoSessionWork never looks at it. Slot writes are by reflection.
                        act[i] = sessions[i];
                        Env.SessionMap[layout == "low" ? i : i * (N / active)] = sessions[i];
                    }
                    int calls = active >= 1000 ? 3000 : active >= 200 ? 12000 : 60000;
                    // serial reference: the same sessions ticked by a plain loop
                    var serial = new Runs();
                    U.Warm(() => { for (int k = 0; k < calls / 4; k++) for (int i = 0; i < act.Length; i++) act[i].TickOutbound(); }, 2);
                    foreach (int dop in new[] { 1, 4 })
                    {
                        po.MaxDegreeOfParallelism = dop;
                        U.Warm(() => { for (int k = 0; k < calls / 4; k++) NetworkManager.DoSessionWork(); }, 3);
                        var runs = new Runs();
                        for (int r = 0; r < 7; r++)
                        {
                            { long a0 = GC.GetTotalAllocatedBytes(true); long t0 = Stopwatch.GetTimestamp(); int cnt = 0; for (int k = 0; k < calls; k++) cnt += NetworkManager.DoSessionWork(); long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetTotalAllocatedBytes(true); runs.Add(U.Ns(t1 - t0) / calls, (double)(a1 - a0) / calls); if (cnt != calls * active) throw new Exception("session count"); }
                            if (dop == 1) { long t0 = Stopwatch.GetTimestamp(); for (int k = 0; k < calls; k++) for (int i = 0; i < act.Length; i++) act[i].TickOutbound(); long t1 = Stopwatch.GetTimestamp(); serial.Add(U.Ns(t1 - t0) / calls, 0); }
                        }
                        Console.WriteLine($"{active,4} sessions, {layout,-18}     {dop,3} {runs.Best,14:F0} {runs.Med,12:F0} {runs.Bytes,9:F1} {runs.Best / active,11:F1} {(dop == 1 ? serial.Best.ToString("F0") : ""),20} {(dop == 1 ? (runs.Best - serial.Best).ToString("F0") : ""),17}");
                    }
                }
            // empty map
            {
                Env.ClearMap(); po.MaxDegreeOfParallelism = 1;
                U.Warm(() => { for (int k = 0; k < 20000; k++) NetworkManager.DoSessionWork(); }, 2);
                var runs = new Runs();
                for (int r = 0; r < 5; r++) { long a0 = GC.GetTotalAllocatedBytes(true); long t0 = Stopwatch.GetTimestamp(); for (int k = 0; k < 60000; k++) NetworkManager.DoSessionWork(); long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetTotalAllocatedBytes(true); runs.Add(U.Ns(t1 - t0) / 60000, (double)(a1 - a0) / 60000); }
                Console.WriteLine($"   0 sessions (empty 1000-slot map)       1 {runs.Best,14:F0} {runs.Med,12:F0} {runs.Bytes,9:F1}");
            }
            Console.WriteLine($"check: S2C packets sent during DoSessionWork measurements = {NetworkStatistics.S2C_Packets_Aggregate - sent1} (must be 0)");
        }
    }

    public static class SystemicMode
    {
        public static void Run(string[] args)
        {
            const int N = 1000;
            double seconds = U.Arg(args, "secs", 4);
            var cs = Env.NewClients(N);
            foreach (var c in cs) c.S.TickOutbound();
            var po = ConfigManager.Config.Server.Threading.NetworkManagerParallelOptions;
            Env.ClearMap(); for (int i = 0; i < N; i++) Env.SessionMap[i] = cs[i].S;

            U.H($"(5) SYSTEMIC: loop {{ NetworkManager.DoSessionWork(); Thread.Sleep(1); }} with {N} real idle in-world sessions, {seconds:F0} s per row");
            Console.WriteLine($"{"variant",-44} {"dop",3} {"iter/s",8} {"CPU ms per wall s",18} {"user",7} {"kernel",7} {"us in DSW/iter",15} {"alloc KB/s",11} {"S2C pkts/s",11} {"gen0",5}");

            // baseline: the sleep loop alone
            Measure("baseline: Thread.Sleep(1) only", 0, seconds, () => { });

            foreach (string variant in new[] { "pure-idle", "natural" })
            {
                var now = DateTime.UtcNow;
                for (int i = 0; i < N; i++)
                {
                    var net = cs[i].Net;
                    if (variant == "pure-idle") { Acc.NextAck(net) = Env.Far; Acc.NextResync(net) = Env.Far; }
                    else
                    {
                        // real timers, staggered the way independently connected clients would be:
                        // 2 s ack interval, 20 s time-sync interval, 5 s cache-prune interval
                        Acc.NextAck(net) = now.AddMilliseconds(i * 2.0);
                        Acc.NextResync(net) = now.AddMilliseconds(i * 20.0);
                        Acc.LastPrune(net) = now.AddMilliseconds(-i * 5.0);
                    }
                }
                foreach (int dop in new[] { 1, 4 })
                {
                    po.MaxDegreeOfParallelism = dop;
                    // warm
                    var w = Stopwatch.StartNew(); while (w.ElapsedMilliseconds < 1500) { NetworkManager.DoSessionWork(); Thread.Sleep(1); }
                    Measure(variant == "pure-idle" ? "pure idle (ack/timesync timers pushed out)" : "natural (2 s acks, 20 s timesync, 5 s prune)", dop, seconds, () => NetworkManager.DoSessionWork());
                }
            }
            // tight loop for reference: how many DoSessionWork per second can one thread do
            {
                foreach (var c in cs) Env.PushTimers(c);
                po.MaxDegreeOfParallelism = 1;
                var sw = Stopwatch.StartNew(); int it = 0; while (sw.ElapsedMilliseconds < 1000) { NetworkManager.DoSessionWork(); it++; }
                Console.WriteLine($"tight loop, no sleep, dop=1, pure idle: {it / sw.Elapsed.TotalSeconds:F0} DoSessionWork/s = {sw.Elapsed.TotalMilliseconds * 1000 / it:F1} us per call");
            }
        }

        static void Measure(string name, int dop, double seconds, Action work)
        {
            GC.Collect(); Thread.Sleep(200);
            var tc0 = ThreadCpu.Snap();
            long pk0 = NetworkStatistics.S2C_Packets_Aggregate;
            long al0 = GC.GetTotalAllocatedBytes(true);
            int g0 = GC.CollectionCount(0);
            var cpu0 = Environment.CpuUsage;
            long inWork = 0; int iters = 0;
            var sw = Stopwatch.StartNew();
            long limit = (long)(seconds * Stopwatch.Frequency);
            while (sw.ElapsedTicks < limit)
            {
                long t0 = Stopwatch.GetTimestamp();
                work();
                inWork += Stopwatch.GetTimestamp() - t0;
                Thread.Sleep(1);
                iters++;
            }
            sw.Stop();
            var cpu1 = Environment.CpuUsage;
            double wall = sw.Elapsed.TotalSeconds;
            double cpuMs = (cpu1.TotalTime - cpu0.TotalTime).TotalMilliseconds / wall;
            double userMs = (cpu1.UserTime - cpu0.UserTime).TotalMilliseconds / wall;
            double kernMs = (cpu1.PrivilegedTime - cpu0.PrivilegedTime).TotalMilliseconds / wall;
            long al1 = GC.GetTotalAllocatedBytes(true);
            var tc1 = ThreadCpu.Snap();
            Console.WriteLine($"{name,-44} {dop,3} {iters / wall,8:F0} {cpuMs,18:F1} {userMs,7:F1} {kernMs,7:F1} {U.Ns(inWork) / 1000 / iters,15:F1} {(al1 - al0) / 1024.0 / wall,11:F1} {(NetworkStatistics.S2C_Packets_Aggregate - pk0) / wall,11:F0} {GC.CollectionCount(0) - g0,5}");
            Console.WriteLine($"      threads: {ThreadCpu.Diff(tc0, tc1, wall, 6)}");
        }
    }
}
