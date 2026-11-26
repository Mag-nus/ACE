using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using ACE.Server.Network;

namespace NetE2E
{
    /// <summary>Per-thread on-CPU time (ns) from /proc/self/task/*/schedstat, keyed by tid, with the thread's comm.</summary>
    public sealed class ThreadCpu
    {
        public readonly Dictionary<int, (string comm, long ns, long switches)> Map = new();
        public static ThreadCpu Snap()
        {
            var t = new ThreadCpu();
            foreach (var dir in Directory.GetDirectories("/proc/self/task"))
            {
                try
                {
                    int tid = int.Parse(Path.GetFileName(dir));
                    string comm = File.ReadAllText(dir + "/comm").Trim();
                    var parts = File.ReadAllText(dir + "/schedstat").Split(' ');
                    t.Map[tid] = (comm, long.Parse(parts[0]), long.Parse(parts[2]));
                }
                catch { }
            }
            return t;
        }
        /// <summary>CPU ms per wall second by thread since 'before', largest first.</summary>
        public static string Diff(ThreadCpu before, ThreadCpu after, double wallSeconds, int top = 4)
        {
            var rows = new List<(string comm, double ms, double sw)>();
            foreach (var kv in after.Map)
            {
                before.Map.TryGetValue(kv.Key, out var b);
                double ms = (kv.Value.ns - b.ns) / 1e6 / wallSeconds;
                if (ms > 0.05) rows.Add((kv.Value.comm + "#" + kv.Key, ms, (kv.Value.switches - b.switches) / wallSeconds));
            }
            return string.Join("  ", rows.OrderByDescending(r => r.ms).Take(top).Select(r => $"[{r.comm}: {r.ms:F1} ms/s, {r.sw:F0} runs/s]"));
        }
    }

    /// <summary>
    /// What does one outbound datagram cost in the server's real main-loop shape (work, then Thread.Sleep(1)) as opposed to a tight loop,
    /// and does it matter that the real listener socket has an async receive pending (it is registered with the runtime's epoll engine)?
    /// </summary>
    public static class SendCostMode
    {
        public static void Run(string[] args)
        {
            double seconds = U.Arg(args, "secs", 3);
            U.H("SENDTO cost by socket and loop shape (bare Socket.SendTo(byte[],int,SocketFlags,EndPoint), 24 B datagram, one destination client that never reads)");
            var plain = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            plain.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var c = Env.NewClient(smallRcv: true);
            var dst = c.S.EndPointS2C; var buf = new byte[1024];
            var socks = new (string name, Socket s)[] { ("plain blocking UDP socket (never used async)", plain), ("REAL S2C ConnectionListener socket (BeginReceiveFrom pending)", Env.L1.Socket) };

            bool quick = U.Arg(args, "quick", 0) != 0;
            Console.WriteLine("  tight loop:");
            foreach (var (name, s) in socks)
            {
                if (quick) break;
                for (int w = 0; w < 3; w++) { for (int i = 0; i < 30000; i++) s.SendTo(buf, 24, SocketFlags.None, dst); Thread.Sleep(300); }
                const int n = 200000;
                var tc0 = ThreadCpu.Snap(); var p0 = Environment.CpuUsage.TotalTime; long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < n; i++) s.SendTo(buf, 24, SocketFlags.None, dst);
                long t1 = Stopwatch.GetTimestamp(); long a1 = GC.GetAllocatedBytesForCurrentThread(); var p1 = Environment.CpuUsage.TotalTime; var tc1 = ThreadCpu.Snap();
                double wall = U.Ns(t1 - t0) / 1e9;
                Console.WriteLine($"    {name,-62} {U.Ns(t1 - t0) / n,8:F0} ns wall/send  {(p1 - p0).TotalMilliseconds * 1e6 / n,8:F0} ns process CPU/send  {(double)(a1 - a0) / n,5:F1} B   threads: {ThreadCpu.Diff(tc0, tc1, wall)}");
            }

            Console.WriteLine($"  main-loop shape: loop {{ K x SendTo; Thread.Sleep(1); }} for {seconds:F0} s:");
            Console.WriteLine($"    {"socket",-62} {"K",3} {"iter/s",7} {"pkts/s",7} {"CPU ms per wall s",18} {"wall us per send",17}   threads (CPU ms per wall s)");
            foreach (int K in new[] { 0, 1, 4 })
                foreach (var (name, s) in socks)
                {
                    if (K == 0 && s != plain) continue;
                    var tc0 = ThreadCpu.Snap(); var p0 = Environment.CpuUsage.TotalTime; var sw = Stopwatch.StartNew(); long inSend = 0; int iters = 0;
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        for (int k = 0; k < K; k++) s.SendTo(buf, 24, SocketFlags.None, dst);
                        inSend += Stopwatch.GetTimestamp() - t0;
                        Thread.Sleep(1); iters++;
                    }
                    double wall = sw.Elapsed.TotalSeconds; var p1 = Environment.CpuUsage.TotalTime; var tc1 = ThreadCpu.Snap();
                    Console.WriteLine($"    {(K == 0 ? "(no send: Sleep(1) only)" : name),-62} {K,3} {iters / wall,7:F0} {iters * K / wall,7:F0} {(p1 - p0).TotalMilliseconds / wall,18:F1} {(K == 0 ? 0 : U.Ns(inSend) / 1000 / (iters * K)),17:F1}   {ThreadCpu.Diff(tc0, tc1, wall)}");
                }
            Env.Remove(c);
        }
    }
}
