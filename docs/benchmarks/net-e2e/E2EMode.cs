using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

using ACE.Server.Network;
using ACE.Server.Network.Managers;

namespace NetE2E
{
    /// <summary>
    /// Bonus: the whole real inbound path INCLUDING the real ConnectionListener receive loop. A real client socket sends datagrams to the
    /// real C2S listener port; ConnectionListener.OnDataReceive (BeginReceiveFrom/EndReceiveFrom, thread pool) does everything else.
    /// </summary>
    public static class E2EMode
    {
        [StructLayout(LayoutKind.Sequential)] struct Timespec { public long Sec; public long Nsec; }
        [DllImport("libc")] static extern int clock_gettime(int clk, out Timespec ts);
        static double ThreadCpuMs() { clock_gettime(3 /* CLOCK_THREAD_CPUTIME_ID */, out var ts); return ts.Sec * 1000.0 + ts.Nsec / 1e6; }

        public static void Run(string[] args)
        {
            int n = (int)U.Arg(args, "n", 100_000);
            U.H($"(e2) INBOUND END TO END through the REAL ConnectionListener receive loop (real UDP datagrams to the real C2S port), n={n} per run");
            Console.WriteLine("    receive-side bytes = GC.GetTotalAllocatedBytes(all threads) minus the sender thread's own; receive-side CPU = process CPU minus the sender thread's CPU.");
            Console.WriteLine("    The sender keeps at most 32 datagrams in flight (the real path needs them in order) and drains InboundMessageQueue as the world thread would.");
            Console.WriteLine($"{"datagram",-40} {"size",5} | {"recv-side B/dgram",18} {"recv-side CPU us/dgram",23} {"wall us/dgram",14} | {"sender B/dgram",15} {"sender CPU us/dgram",20}");
            foreach (var (kind, name) in new[] { (InKind.AckEnc, "ack-only, encrypted"), (InKind.Action64, "one action fragment (64 B)"), (InKind.AckAction64, "ack + action") })
            {
                Runs bytes = new(), cpu = new(), wall = new(), sb = new(), scpu = new(); int stride = 0;
                for (int r = -2; r < 4; r++)
                {
                    int cnt = r < 0 ? 30000 : n;
                    var b = InboundMode.Build(kind, cnt, 0); var c = b.C; var net = c.Net; stride = b.Stride;
                    var dst = Env.L0.ListenerEndpoint; var queue = NetworkManager.InboundMessageQueue;
                    long crc0 = NetworkStatistics.C2S_CRCErrors_Aggregate;
                    long tot0 = GC.GetTotalAllocatedBytes(true), my0 = GC.GetAllocatedBytesForCurrentThread();
                    var p0 = Environment.CpuUsage.TotalTime; double th0 = ThreadCpuMs(); long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < cnt; i++)
                    {
                        int spins = 0;
                        while (i - (int)(Acc.LastRecvSeq(net) - 1) >= 32) { if (++spins > 2000) Thread.Yield(); else Thread.SpinWait(20); }
                        c.Sock.SendTo(b.Flat, i * stride, stride, SocketFlags.None, dst);
                        if ((i & 31) == 31) queue.RunActions();
                    }
                    var deadline = Stopwatch.StartNew();
                    while (Acc.LastRecvSeq(net) != (uint)cnt + 1) { Thread.Yield(); if (deadline.ElapsedMilliseconds > 5000) throw new Exception($"e2e stalled at {Acc.LastRecvSeq(net)} of {cnt + 1} (datagram loss)"); }
                    long t1 = Stopwatch.GetTimestamp(); double th1 = ThreadCpuMs(); var p1 = Environment.CpuUsage.TotalTime;
                    long my1 = GC.GetAllocatedBytesForCurrentThread(), tot1 = GC.GetTotalAllocatedBytes(true);
                    queue.RunActions();
                    if (NetworkStatistics.C2S_CRCErrors_Aggregate != crc0) throw new Exception("CRC errors");
                    if (r >= 0)
                    {
                        bytes.Add(0, (double)((tot1 - tot0) - (my1 - my0)) / cnt);
                        cpu.Add(((p1 - p0).TotalMilliseconds - (th1 - th0)) * 1000 / cnt, 0);
                        wall.Add(U.Ns(t1 - t0) / 1000 / cnt, 0);
                        sb.Add(0, (double)(my1 - my0) / cnt);
                        scpu.Add((th1 - th0) * 1000 / cnt, 0);
                    }
                    Env.Remove(c);
                    if (r < 0) Thread.Sleep(300);
                }
                Console.WriteLine($"{name,-40} {stride,5} | {bytes.Bytes,18:F1} {cpu.Med,23:F2} {wall.Med,14:F2} | {sb.Bytes,15:F1} {scpu.Med,20:F2}");
            }
        }
    }
}
