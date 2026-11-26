using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;

using ACE.Server.Network;
using ACE.Server.Network.Managers;

namespace NetE2E
{
    /// <summary>
    /// In-process listener on the runtime provider. Records GCAllocationTick (GC keyword 0x1, Verbose: one event per ~100 KB allocated,
    /// attributed to the object that crossed the threshold) and, where the runtime emits it (.NET 10), AllocationSampled
    /// (keyword 0x80000000000: unbiased per-byte random sampling, mean one sample per ~100 KB).
    /// </summary>
    public sealed class AllocListener : EventListener
    {
        public struct Ev { public DateTime Ts; public long Tid; public string Type; public long Amount; public long ObjSize; public bool Sampled; }
        public static readonly ConcurrentQueue<Ev> Events = new();
        public static volatile bool Recording;
        public static long Keywords = 0x1 | 0x80000000000;   // must be set before the first instance is created
        public static readonly ConcurrentDictionary<string, int> Names = new();

        protected override void OnEventSourceCreated(EventSource src)
        {
            if (src.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(src, EventLevel.Verbose, (EventKeywords)Keywords);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName != null) Names.AddOrUpdate(e.EventName, 1, (k, v) => v + 1);
            if (!Recording || e.EventName == null) return;
            bool tick = e.EventName.StartsWith("GCAllocationTick");
            bool sampled = e.EventName == "AllocationSampled";
            if (!tick && !sampled) return;
            var names = e.PayloadNames; var p = e.Payload;
            string type = null; long amount = 0, size = 0;
            for (int i = 0; i < names.Count; i++)
            {
                switch (names[i])
                {
                    case "TypeName": type = p[i] as string; break;
                    case "AllocationAmount64": amount = Convert.ToInt64(p[i]); break;
                    case "ObjectSize": size = Convert.ToInt64(p[i]); break;
                }
            }
            Events.Enqueue(new Ev { Ts = e.TimeStamp, Tid = e.OSThreadId, Type = type ?? "?", Amount = amount, ObjSize = size, Sampled = sampled });
        }
    }

    public static class TypesMode
    {
        static readonly List<(DateTime s, DateTime e)> windows = new();

        static void Report(string title, long tid, long exactBytes, long ops, string opName)
        {
            Thread.Sleep(1500); // let the dispatch thread deliver everything
            AllocListener.Recording = false;
            var all = new List<AllocListener.Ev>();
            while (AllocListener.Events.TryDequeue(out var ev)) if (windows.Any(w => ev.Ts >= w.s && ev.Ts <= w.e)) all.Add(ev);
            windows.Clear();
            AllocListener.Recording = true;
            var mine = all.Where(x => x.Tid == tid).ToList();
            Console.WriteLine();
            Console.WriteLine($"--- {title}");
            Console.WriteLine($"    exact: {exactBytes:N0} B over {ops:N0} {opName} = {(double)exactBytes / ops:F1} B/{opName}   (events: {all.Count} total, {mine.Count} on the measuring thread {tid}; other threads: {string.Join(", ", all.Where(x => x.Tid != tid).GroupBy(x => x.Tid).Select(g => g.Key + "x" + g.Count()))})");
            foreach (bool sampled in new[] { false, true })
            {
                var evs = mine.Where(x => x.Sampled == sampled).ToList();
                string src = sampled ? "AllocationSampled (random per-byte sampling, share by sample count)" : "GCAllocationTick (share by AllocationAmount64)";
                if (evs.Count == 0) { Console.WriteLine($"    {src}: no events"); continue; }
                double total = sampled ? evs.Count : evs.Sum(x => (double)x.Amount);
                Console.WriteLine($"    {src}: {evs.Count} events{(sampled ? "" : $", covering {total:N0} B = {total / exactBytes * 100:F1}% of the exact total")}");
                Console.WriteLine($"      {"share",7} {"B/" + opName,10} {"obj size",9}  type");
                int rank = 0;
                foreach (var g in evs.GroupBy(x => x.Type).Select(g => new { Type = g.Key, W = sampled ? g.Count() : g.Sum(x => (double)x.Amount), Sz = g.Select(x => x.ObjSize).Where(s => s > 0).DefaultIfEmpty(0).Min(), SzMax = g.Select(x => x.ObjSize).DefaultIfEmpty(0).Max() }).OrderByDescending(x => x.W))
                {
                    if (++rank > 22) break;
                    double share = g.W / total;
                    string sz = g.Sz == 0 ? "" : g.Sz == g.SzMax ? g.Sz.ToString() : g.Sz + "-" + g.SzMax;
                    Console.WriteLine($"      {share * 100,6:F1}% {share * exactBytes / ops,10:F1} {sz,9}  {g.Type}");
                }
            }
        }

        public static void Run(string[] args)
        {
            string sampler = U.ArgS(args, "sampler", "sampled");
            AllocListener.Keywords = sampler == "tick" ? 0x1 : (0x1 | 0x80000000000);
            Console.WriteLine($"[types] sampler={sampler}: keywords 0x{AllocListener.Keywords:X} at Verbose" + (sampler == "tick" ? " (GCAllocationTick only)" : " (AllocationSampled; with this keyword on, .NET 10 does not emit GCAllocationTick)"));
            var listener = new AllocListener();
            long tid = U.GetTid();
            U.H("(3) ALLOCATION BREAKDOWN BY TYPE on the real path (runtime allocation events, measuring thread only)");

            // ---------- (b) 6 small messages ----------
            {
                int n = (int)U.Arg(args, "nflush", 1_200_000);
                var sc = Sc.All()[1];
                var c = Env.NewClient(smallRcv: true);
                c.S.TickOutbound(); Env.PushTimers(c);
                var net = c.Net; uint trimLo = 2;
                Action<int> body = cnt =>
                {
                    for (int i = 0; i < cnt; i++)
                    {
                        net.EnqueueSend(sc.Msgs);
                        Acc.NextSend(net) = default;
                        net.Update();
                        if ((i & 31) == 0) { uint cur = c.CurSeq; while (trimLo + 8 < cur) c.Cache.TryRemove(trimLo++, out _); }
                    }
                };
                U.Warm(() => body(20000));
                AllocListener.Recording = true;
                Thread.Sleep(300);
                var w0 = DateTime.UtcNow;
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                body(n);
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                windows.Add((w0, DateTime.UtcNow));
                Report($"flush '6 small messages' (EnqueueSend(msgs[]) + real Update()), {n:N0} flushes", tid, a1 - a0, n, "flush");
                Env.Remove(c);
            }

            // ---------- (e) one action fragment ----------
            {
                int per = (int)U.Arg(args, "ninbound", 400_000); int batches = 5;
                var rx = new byte[ClientPacket.MaxPacketSize];
                // warm
                for (int w = 0; w < 3; w++) { var b = InboundMode.Build(InKind.Action64, 30000, 0); Feed(b, rx); Env.Remove(b.C); Thread.Sleep(300); }
                long exact = 0; long ops = 0;
                for (int k = 0; k < batches; k++)
                {
                    var b = InboundMode.Build(InKind.Action64, per, 0);   // built outside the recorded time window
                    Thread.Sleep(50);
                    var w0 = DateTime.UtcNow;
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    Feed(b, rx);
                    long a1 = GC.GetAllocatedBytesForCurrentThread();
                    windows.Add((w0, DateTime.UtcNow));
                    Thread.Sleep(50);
                    exact += a1 - a0; ops += per;
                    if (Acc.LastRecvFragSeq(b.C.Net) != (uint)per) throw new Exception("inbound batch incomplete");
                    Env.Remove(b.C);
                }
                Report($"inbound 'one action fragment' (64 B GameAction): new ClientPacket + Unpack + NetworkManager.ProcessPacket + ReleaseBuffer, plus InboundMessageQueue.RunActions every 16; {ops:N0} datagrams", tid, exact, ops, "dgram");
            }
            Console.WriteLine();
            Console.WriteLine("    closure legend (reflection over ACE.Server):");
            foreach (var t in typeof(ACE.Server.Network.NetworkSession).Assembly.GetTypes())
                if (t.DeclaringType != null && (t.DeclaringType == typeof(NetworkManager) || t.DeclaringType == typeof(InboundMessageManager) || t.DeclaringType == typeof(NetworkSession)) && t.Name.StartsWith("<>c__DisplayClass"))
                    Console.WriteLine($"      {t.DeclaringType.Name}+{t.Name}: fields {string.Join(", ", t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Select(f => f.FieldType.Name + " " + f.Name))}");
            Console.WriteLine("    runtime events seen by the listener (all threads, whole run): " + string.Join(", ", AllocListener.Names.OrderByDescending(k => k.Value).Select(k => k.Key + " x" + k.Value)));
            GC.KeepAlive(listener);
        }

        static void Feed(InboundMode.Batch b, byte[] rx)
        {
            var c = b.C; var ep = c.Ep; var l0 = Env.L0; var queue = NetworkManager.InboundMessageQueue; int stride = b.Stride;
            for (int i = 0; i < b.N; i++)
            {
                Buffer.BlockCopy(b.Flat, i * stride, rx, 0, stride);
                var packet = new ClientPacket();
                if (packet.Unpack(rx, stride))
                    NetworkManager.ProcessPacket(l0, packet, ep);
                packet.ReleaseBuffer();
                if ((i & 15) == 15) queue.RunActions();
            }
            queue.RunActions();
        }
    }
}
